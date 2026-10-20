using System.Collections.Immutable;

namespace MediaDashboard.Core;

public enum EditError
{
    /// <summary>The last enabled Display cannot be disabled.</summary>
    LastActiveDisplay,

    /// <summary>A Mirror Group can only contain Displays on the same graphics adapter.</summary>
    DifferentAdapters,
}

public sealed record EditResult(DisplayLayout Layout, EditError? Error = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Draft operations on a Layout. Every operation is pure and returns a Layout that satisfies
/// the <see cref="LayoutEngine"/> geometry rules, or the unchanged Layout with an error.
/// </summary>
public sealed class DisplayEditor
{
    private readonly IReadOnlyDictionary<DisplayId, DisplayInfo> _displays;

    public DisplayEditor(IEnumerable<DisplayInfo> connected) =>
        _displays = connected.ToDictionary(d => d.Id);

    /// <summary>Click on a single Display or on a member name inside a Mirror Group.</summary>
    public EditResult DisableDisplay(DisplayLayout layout, DisplayId id)
    {
        var item = layout.ItemOf(id);
        if (item is null) return new(layout);
        if (!item.IsMirrorGroup) return DisableItem(layout, id);

        var shrunk = item with { Members = item.Members.Remove(id) };
        return new(new DisplayLayout(layout.Items.Replace(item, shrunk)));
    }

    /// <summary>Click on a tile background: disables the whole Display or Mirror Group containing <paramref name="anyMember"/>.</summary>
    public EditResult DisableItem(DisplayLayout layout, DisplayId anyMember)
    {
        var item = layout.ItemOf(anyMember);
        if (item is null) return new(layout);
        if (layout.Items.Length == 1) return new(layout, EditError.LastActiveDisplay);

        var rest = layout.Items.Remove(item);
        if (item.IsPrimary) rest = MakePrimary(rest, Leftmost(rest));
        return new(LayoutEngine.Relayout(new DisplayLayout(rest)));
    }

    /// <summary>Click on a disabled Display: enables it to the right of the rightmost enabled item.</summary>
    public EditResult Enable(DisplayLayout layout, DisplayId id)
    {
        if (layout.IsEnabled(id)) return new(layout);
        var size = _displays[id].PreferredSize;

        if (layout.Items.IsEmpty)
            return new(new DisplayLayout([new LayoutItem([id], new PixelRect(0, 0, size.Width, size.Height), true)]));

        var rightmost = layout.Items.MaxBy(i => i.Bounds.Right)!;
        var bounds = LayoutEngine.Snap(LayoutEngine.Beside(rightmost.Bounds, size, Side.Right), Rects(layout.Items));
        return new(new DisplayLayout(layout.Items.Add(new LayoutItem([id], bounds, false))));
    }

    /// <summary>Double click: makes the item containing <paramref name="id"/> Primary, enabling the Display first if needed.</summary>
    public EditResult SetPrimary(DisplayLayout layout, DisplayId id)
    {
        layout = Enable(layout, id).Layout;
        var item = layout.ItemOf(id)!;
        return new(LayoutEngine.Relayout(new DisplayLayout(MakePrimary(layout.Items, item))));
    }

    /// <summary>
    /// Drop <paramref name="dragged"/> onto <paramref name="target"/> and pick "Duplicate".
    /// <paramref name="dragged"/> is a whole tile, one member pulled out of a Mirror Group, or a disabled Display.
    /// </summary>
    public EditResult Mirror(DisplayLayout layout, ImmutableArray<DisplayId> dragged, DisplayId target)
    {
        if (dragged.Contains(target) || !layout.IsEnabled(target)) return new(layout);
        string adapter = _displays[target].AdapterKey;
        if (dragged.Any(d => _displays[d].AdapterKey != adapter)) return new(layout, EditError.DifferentAdapters);

        var (items, _, carriedPrimary) = Detach(layout, dragged);
        var targetItem = items.First(i => i.Contains(target));
        var merged = targetItem with { Members = targetItem.Members.AddRange(dragged) };
        items = items.Replace(targetItem, merged);
        if (carriedPrimary) items = MakePrimary(items, merged);
        return new(LayoutEngine.Relayout(new DisplayLayout(items)));
    }

    /// <summary>Drop <paramref name="dragged"/> onto <paramref name="target"/> and pick "Extend": placed flush against <paramref name="side"/>.</summary>
    public EditResult Extend(DisplayLayout layout, ImmutableArray<DisplayId> dragged, DisplayId target, Side side)
    {
        if (dragged.Contains(target) || !layout.IsEnabled(target)) return new(layout);

        var (items, size, carriedPrimary) = Detach(layout, dragged);
        var targetBounds = items.First(i => i.Contains(target)).Bounds;
        var bounds = LayoutEngine.Beside(targetBounds, size, side);
        return Place(items, dragged, bounds, carriedPrimary);
    }

    /// <summary>Drop <paramref name="dragged"/> on empty space: snaps to the nearest free edge.</summary>
    public EditResult Move(DisplayLayout layout, ImmutableArray<DisplayId> dragged, int x, int y)
    {
        var (items, size, carriedPrimary) = Detach(layout, dragged);
        return Place(items, dragged, new PixelRect(x, y, size.Width, size.Height), carriedPrimary);
    }

    /// <summary>"Ungroup": every member becomes its own Display, lined up to the right of the first one.</summary>
    public EditResult Ungroup(DisplayLayout layout, DisplayId anyMember)
    {
        var group = layout.ItemOf(anyMember);
        if (group is null || !group.IsMirrorGroup) return new(layout);

        var items = layout.Items.Remove(group);
        var previous = Sized(group.Bounds, group.Members[0]);
        items = items.Add(new LayoutItem([group.Members[0]], previous, group.IsPrimary));
        foreach (var member in group.Members.Skip(1))
        {
            var bounds = LayoutEngine.Beside(previous, _displays[member].PreferredSize, Side.Right);
            items = items.Add(new LayoutItem([member], bounds, false));
            previous = bounds;
        }
        return new(LayoutEngine.Relayout(new DisplayLayout(items)));
    }

    /// <summary>
    /// Removes the dragged displays from wherever they are. Returns the remaining items, the size the
    /// dragged tile should keep, and whether it carried the Primary flag away with it.
    /// </summary>
    private (ImmutableArray<LayoutItem> Items, PixelSize Size, bool CarriedPrimary) Detach(
        DisplayLayout layout, ImmutableArray<DisplayId> dragged)
    {
        var source = layout.ItemOf(dragged[0]);
        var preferred = _displays[dragged[0]].PreferredSize;
        if (source is null) return (layout.Items, preferred, false);

        if (source.Members.Length == dragged.Length)
        {
            var size = new PixelSize(source.Bounds.Width, source.Bounds.Height);
            return (layout.Items.Remove(source), size, source.IsPrimary);
        }

        var shrunk = source with { Members = source.Members.RemoveRange(dragged) };
        return (layout.Items.Replace(source, shrunk), preferred, false);
    }

    private static EditResult Place(ImmutableArray<LayoutItem> items, ImmutableArray<DisplayId> members, PixelRect desired, bool primary)
    {
        var rects = Rects(items);
        bool valid = rects.Count == 0 || (!rects.Any(desired.Overlaps) && rects.Any(desired.Touches));
        var bounds = valid ? desired : LayoutEngine.Snap(desired, rects);
        var moved = new LayoutItem(members, bounds, primary);
        items = items.Add(moved);
        if (primary || !items.Any(i => i.IsPrimary)) items = MakePrimary(items, primary ? moved : Leftmost(items));
        return new(LayoutEngine.Relayout(new DisplayLayout(items)));
    }

    private PixelRect Sized(PixelRect at, DisplayId id)
    {
        var size = _displays[id].PreferredSize;
        return new PixelRect(at.X, at.Y, size.Width, size.Height);
    }

    private static ImmutableArray<LayoutItem> MakePrimary(ImmutableArray<LayoutItem> items, LayoutItem primary) =>
        items.Select(i => i with { IsPrimary = ReferenceEquals(i, primary) }).ToImmutableArray();

    internal static LayoutItem Leftmost(IEnumerable<LayoutItem> items) =>
        items.OrderBy(i => i.Bounds.X).ThenBy(i => i.Bounds.Y).First();

    private static List<PixelRect> Rects(IEnumerable<LayoutItem> items) => items.Select(i => i.Bounds).ToList();
}
