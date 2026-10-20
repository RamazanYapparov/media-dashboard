using System.Collections.Immutable;

namespace MediaDashboard.Core;

/// <summary>
/// Geometry rules of a Layout: every item touches the rest (Windows does not allow gaps),
/// no two items overlap, and the Primary sits at the desktop origin.
/// </summary>
public static class LayoutEngine
{
    /// <summary>
    /// Moves <paramref name="desired"/> to the nearest position that touches at least one of
    /// <paramref name="others"/> and overlaps none of them. Near-aligned edges snap flush.
    /// </summary>
    public static PixelRect Snap(PixelRect desired, IReadOnlyList<PixelRect> others)
    {
        if (others.Count == 0) return desired;

        PixelRect? best = null;
        double bestDistance = double.MaxValue;
        foreach (var o in others)
        {
            foreach (var candidate in CandidatesAround(o, desired, others))
            {
                if (others.Any(candidate.Overlaps)) continue;
                double d = Distance(candidate, desired);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = candidate;
                }
            }
        }

        // Every candidate collided (crowded layout): the space right of the rightmost item is always free.
        var rightmost = others.MaxBy(o => o.Right)!;
        return best ?? desired.At(rightmost.Right, rightmost.Y);
    }

    /// <summary>A rectangle of <paramref name="size"/> flush against <paramref name="side"/> of <paramref name="target"/>, edges aligned.</summary>
    public static PixelRect Beside(PixelRect target, PixelSize size, Side side) => side switch
    {
        Side.Right => new PixelRect(target.Right, target.Y, size.Width, size.Height),
        Side.Left => new PixelRect(target.X - size.Width, target.Y, size.Width, size.Height),
        Side.Bottom => new PixelRect(target.X, target.Bottom, size.Width, size.Height),
        Side.Top => new PixelRect(target.X, target.Y - size.Height, size.Width, size.Height),
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };

    /// <summary>
    /// Restores the geometry rules after items were removed, added or resized, keeping every item
    /// as close as possible to where it was relative to its nearest neighbour.
    /// An already valid Layout is only translated so the Primary is at the origin.
    /// </summary>
    public static DisplayLayout Relayout(DisplayLayout layout)
    {
        if (layout.Items.IsEmpty) return layout;

        var items = layout.Items;
        int primaryIndex = items.IndexOf(layout.Primary ?? items[0]);
        var placed = new Dictionary<int, PixelRect> { [primaryIndex] = items[primaryIndex].Bounds };
        var remaining = Enumerable.Range(0, items.Length).Where(i => i != primaryIndex).ToList();

        while (remaining.Count > 0)
        {
            // Place the item closest to what is already placed, following its nearest placed neighbour.
            var (next, anchor) = remaining
                .SelectMany(r => placed.Keys.Select(p => (r, p)))
                .MinBy(pair => items[pair.r].Bounds.GapTo(items[pair.p].Bounds));

            var anchorShift = (dx: placed[anchor].X - items[anchor].Bounds.X, dy: placed[anchor].Y - items[anchor].Bounds.Y);
            var desired = items[next].Bounds.Offset(anchorShift.dx, anchorShift.dy);
            var placedRects = placed.Values.ToList();

            bool valid = !placedRects.Any(desired.Overlaps) && placedRects.Any(desired.Touches);
            placed[next] = valid ? desired : Snap(desired, placedRects);
            remaining.Remove(next);
        }

        var primary = placed[primaryIndex];
        var result = items.Select((item, i) => item with
        {
            Bounds = placed[i].Offset(-primary.X, -primary.Y),
            IsPrimary = i == primaryIndex,
        });
        return new DisplayLayout(result.ToImmutableArray());
    }

    private static IEnumerable<PixelRect> CandidatesAround(PixelRect o, PixelRect desired, IReadOnlyList<PixelRect> others)
    {
        int w = desired.Width, h = desired.Height;
        // Share at least a quarter of the shorter edge: a 1 px contact is legal but useless.
        int minOverlapY = Math.Max(1, Math.Min(h, o.Height) / 4);
        int minOverlapX = Math.Max(1, Math.Min(w, o.Width) / 4);
        int yLo = o.Y - h + minOverlapY, yHi = o.Bottom - minOverlapY;
        int xLo = o.X - w + minOverlapX, xHi = o.Right - minOverlapX;
        int y = Align(Math.Clamp(desired.Y, yLo, yHi), h, yLo, yHi, others.Select(r => (r.Y, r.Bottom)));
        int x = Align(Math.Clamp(desired.X, xLo, xHi), w, xLo, xHi, others.Select(r => (r.X, r.Right)));
        yield return new PixelRect(o.Right, y, w, h);
        yield return new PixelRect(o.X - w, y, w, h);
        yield return new PixelRect(x, o.Bottom, w, h);
        yield return new PixelRect(x, o.Y - h, w, h);
    }

    /// <summary>
    /// Pulls <paramref name="pos"/> onto the nearest edge of any neighbour (start-to-start, start-to-end,
    /// end-to-end, end-to-start) within 1/8 of the length, staying inside [lo, hi].
    /// </summary>
    private static int Align(int pos, int length, int lo, int hi, IEnumerable<(int Start, int End)> edges)
    {
        int best = pos, bestDelta = length / 8 + 1;
        foreach (var (start, end) in edges)
        {
            foreach (int candidate in (ReadOnlySpan<int>)[start, end, end - length, start - length])
            {
                int delta = Math.Abs(candidate - pos);
                if (delta < bestDelta && candidate >= lo && candidate <= hi)
                {
                    best = candidate;
                    bestDelta = delta;
                }
            }
        }
        return best;
    }

    private static double Distance(PixelRect a, PixelRect b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
