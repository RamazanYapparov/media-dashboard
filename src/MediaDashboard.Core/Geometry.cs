namespace MediaDashboard.Core;

/// <summary>Rectangle in desktop pixels (the coordinate space Windows uses for monitors).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public PixelRect At(int x, int y) => this with { X = x, Y = y };

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>True when the interiors intersect; touching edges do not count.</summary>
    public bool Overlaps(PixelRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>True when the rectangles share a non-degenerate piece of an edge.</summary>
    public bool Touches(PixelRect other)
    {
        bool verticalEdge = (Right == other.X || other.Right == X) && Y < other.Bottom && other.Y < Bottom;
        bool horizontalEdge = (Bottom == other.Y || other.Bottom == Y) && X < other.Right && other.X < Right;
        return verticalEdge || horizontalEdge;
    }

    /// <summary>Distance between the closest points of two rectangles; zero when touching or overlapping.</summary>
    public double GapTo(PixelRect other)
    {
        int dx = Math.Max(0, Math.Max(other.X - Right, X - other.Right));
        int dy = Math.Max(0, Math.Max(other.Y - Bottom, Y - other.Bottom));
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }
}

public readonly record struct PixelSize(int Width, int Height);

public enum Side { Left, Right, Top, Bottom }
