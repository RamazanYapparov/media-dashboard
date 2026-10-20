using System.Collections.Immutable;
using MediaDashboard.Core;

namespace MediaDashboard.Core.Tests;

public class DisplayEditorTests
{
    private static readonly DisplayId Left = new("left"), Right = new("right"), Tv = new("tv"), Other = new("other-gpu");

    private static readonly DisplayInfo[] Connected =
    [
        new(Left, "Mi Monitor", "amd", new PixelSize(2560, 1440)),
        new(Right, "Mi Monitor", "amd", new PixelSize(2560, 1440)),
        new(Tv, "Mi TV", "amd", new PixelSize(3840, 2160)),
        new(Other, "Dell", "nvidia", new PixelSize(1920, 1080)),
    ];

    private readonly DisplayEditor _editor = new(Connected);

    /// <summary>Left | Right (primary) | TV, top-aligned.</summary>
    private static DisplayLayout ThreeInARow() => new(
    [
        new LayoutItem([Left], new PixelRect(-2560, 0, 2560, 1440), false),
        new LayoutItem([Right], new PixelRect(0, 0, 2560, 1440), true),
        new LayoutItem([Tv], new PixelRect(2560, 0, 3840, 2160), false),
    ]);

    private static void AssertValidGeometry(DisplayLayout layout)
    {
        Assert.Single(layout.Items, i => i.IsPrimary);
        Assert.Equal(new PixelRect(0, 0, layout.Primary!.Bounds.Width, layout.Primary.Bounds.Height), layout.Primary.Bounds);
        var rects = layout.Items.Select(i => i.Bounds).ToList();
        for (int i = 0; i < rects.Count; i++)
        for (int j = i + 1; j < rects.Count; j++)
            Assert.False(rects[i].Overlaps(rects[j]), $"{rects[i]} overlaps {rects[j]}");
        if (rects.Count > 1)
            Assert.All(rects, r => Assert.Contains(rects, o => o != r && o.Touches(r)));
    }

    [Fact]
    public void Disabling_the_last_enabled_display_is_rejected()
    {
        var single = new DisplayLayout([new LayoutItem([Right], new PixelRect(0, 0, 2560, 1440), true)]);

        var result = _editor.DisableDisplay(single, Right);

        Assert.Equal(EditError.LastActiveDisplay, result.Error);
        Assert.Same(single, result.Layout);
    }

    [Fact]
    public void Disabling_the_primary_makes_the_leftmost_remaining_display_primary()
    {
        var result = _editor.DisableDisplay(ThreeInARow(), Right);

        Assert.True(result.Ok);
        Assert.Equal([Left], result.Layout.Primary!.Members.AsEnumerable());
        Assert.False(result.Layout.IsEnabled(Right));
        AssertValidGeometry(result.Layout);
    }

    [Fact]
    public void Disabling_a_middle_display_closes_the_gap()
    {
        var layout = _editor.DisableDisplay(ThreeInARow(), Right).Layout;

        var left = layout.ItemOf(Left)!.Bounds;
        var tv = layout.ItemOf(Tv)!.Bounds;
        Assert.Equal(left.Right, tv.X);
    }

    [Fact]
    public void Enabling_places_the_display_right_of_the_rightmost_one()
    {
        var withoutTv = _editor.DisableDisplay(ThreeInARow(), Tv).Layout;

        var layout = _editor.Enable(withoutTv, Tv).Layout;

        Assert.Equal(new PixelRect(2560, 0, 3840, 2160), layout.ItemOf(Tv)!.Bounds);
        Assert.False(layout.ItemOf(Tv)!.IsPrimary);
    }

    [Fact]
    public void Double_click_on_a_disabled_display_enables_it_and_makes_it_primary()
    {
        var withoutTv = _editor.DisableDisplay(ThreeInARow(), Tv).Layout;

        var layout = _editor.SetPrimary(withoutTv, Tv).Layout;

        Assert.Equal([Tv], layout.Primary!.Members.AsEnumerable());
        Assert.Equal(3, layout.Items.Length);
        AssertValidGeometry(layout);
    }

    [Fact]
    public void Duplicating_onto_a_display_forms_one_mirror_group_tile()
    {
        var result = _editor.Mirror(ThreeInARow(), [Tv], Right);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Layout.Items.Length);
        Assert.Equal([Right, Tv], result.Layout.ItemOf(Tv)!.Members.AsEnumerable());
        Assert.True(result.Layout.ItemOf(Tv)!.IsPrimary);
        AssertValidGeometry(result.Layout);
    }

    [Fact]
    public void Dragging_the_primary_into_a_group_keeps_the_group_primary()
    {
        var layout = _editor.Mirror(ThreeInARow(), [Right], Left).Layout;

        Assert.True(layout.ItemOf(Left)!.IsPrimary);
        Assert.Equal([Left, Right], layout.ItemOf(Right)!.Members.AsEnumerable());
    }

    [Fact]
    public void Duplicating_across_graphics_adapters_is_rejected()
    {
        var layout = _editor.Enable(ThreeInARow(), Other).Layout;

        var result = _editor.Mirror(layout, [Other], Right);

        Assert.Equal(EditError.DifferentAdapters, result.Error);
    }

    [Fact]
    public void Clicking_a_member_name_removes_only_that_member_and_the_group_stays_primary()
    {
        var grouped = _editor.Mirror(ThreeInARow(), [Tv], Right).Layout;

        var layout = _editor.DisableDisplay(grouped, Tv).Layout;

        Assert.False(layout.IsEnabled(Tv));
        Assert.Equal([Right], layout.ItemOf(Right)!.Members.AsEnumerable());
        Assert.True(layout.ItemOf(Right)!.IsPrimary);
    }

    [Fact]
    public void Clicking_a_group_background_disables_every_member()
    {
        var grouped = _editor.Mirror(ThreeInARow(), [Tv], Right).Layout;

        var layout = _editor.DisableItem(grouped, Tv).Layout;

        Assert.False(layout.IsEnabled(Tv));
        Assert.False(layout.IsEnabled(Right));
        Assert.Equal([Left], layout.Primary!.Members.AsEnumerable());
    }

    [Fact]
    public void Extend_places_the_display_flush_against_the_chosen_side()
    {
        var layout = _editor.Extend(ThreeInARow(), [Tv], Left, Side.Left).Layout;

        var left = layout.ItemOf(Left)!.Bounds;
        var tv = layout.ItemOf(Tv)!.Bounds;
        Assert.Equal(left.X, tv.Right);
        Assert.Equal(left.Y, tv.Y);
        AssertValidGeometry(layout);
    }

    [Fact]
    public void Pulling_a_member_out_of_a_group_extends_it_where_it_was_dropped()
    {
        var grouped = _editor.Mirror(ThreeInARow(), [Tv], Right).Layout;

        var layout = _editor.Extend(grouped, [Tv], Right, Side.Bottom).Layout;

        Assert.Equal([Right], layout.ItemOf(Right)!.Members.AsEnumerable());
        Assert.Equal(layout.ItemOf(Right)!.Bounds.Bottom, layout.ItemOf(Tv)!.Bounds.Y);
        AssertValidGeometry(layout);
    }

    [Fact]
    public void Moving_into_empty_space_snaps_to_the_nearest_edge_without_gaps()
    {
        // Dropped a little below and right of where Right's bottom-left corner is.
        var layout = _editor.Move(ThreeInARow(), [Left], 100, 1600).Layout;

        Assert.Equal(new PixelRect(0, 1440, 2560, 1440), layout.ItemOf(Left)!.Bounds);
        AssertValidGeometry(layout);
    }

    [Fact]
    public void Moving_onto_another_display_never_overlaps()
    {
        var layout = _editor.Move(ThreeInARow(), [Tv], 100, 100).Layout;

        AssertValidGeometry(layout);
    }

    [Fact]
    public void Ungroup_lines_members_up_and_keeps_primary()
    {
        var grouped = _editor.Mirror(ThreeInARow(), [Tv], Right).Layout;

        var layout = _editor.Ungroup(grouped, Tv).Layout;

        Assert.Equal(3, layout.Items.Length);
        Assert.Equal([Right], layout.Primary!.Members.AsEnumerable());
        Assert.Equal(layout.ItemOf(Right)!.Bounds.Right, layout.ItemOf(Tv)!.Bounds.X);
        AssertValidGeometry(layout);
    }

    [Fact]
    public void A_dropped_tile_on_itself_changes_nothing()
    {
        var layout = ThreeInARow();

        Assert.Same(layout, _editor.Mirror(layout, [Tv], Tv).Layout);
        Assert.Same(layout, _editor.Extend(layout, [Tv], Tv, Side.Left).Layout);
    }
}
