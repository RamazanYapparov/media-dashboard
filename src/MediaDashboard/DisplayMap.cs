using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MediaDashboard.Core;

namespace MediaDashboard;

/// <summary>
/// The display schema: enabled Displays and Mirror Groups drawn proportionally to their resolution
/// where they sit on the desktop (decision Q23), plus the "Disabled" strip (Q18).
/// Clicks, double clicks and drag &amp; drop are turned into Draft edits on the controller.
/// </summary>
public sealed class DisplayMap : UserControl
{
    private const double MapHeight = 150, Inset = 10, StripTileHeight = 40, DragThreshold = 5;

    /// <summary>Within this many canvas pixels of its landing spot the dragged ghost snaps onto it.</summary>
    private const double MagnetRadius = 14;

    private readonly Canvas _canvas = new() { Height = MapHeight, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly WrapPanel _strip = new();
    private readonly Border _stripArea;
    private readonly TextBlock _stripLabel;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Canvas _preview = new() { IsHitTestVisible = false, Height = MapHeight, ClipToBounds = true };
    private string _stripLabelText = "";
    private readonly DispatcherTimer _clickTimer = new();
    private readonly List<(Rect Rect, LayoutItem Item)> _tiles = [];

    private AppController? _controller;
    private Action? _pendingClick;
    private Press? _press;

    // Desktop → canvas transform of the last render; kept fixed during a drag.
    private double _scale, _offsetX, _offsetY;
    private int _minX, _minY;

    public DisplayMap()
    {
        _stripLabel = new TextBlock { Margin = new Thickness(0, 10, 0, 4) };
        _stripLabel.SetResourceReference(StyleProperty, "Subtle");
        _stripArea = new Border { Child = _strip, MinHeight = StripTileHeight, Background = Brushes.Transparent, CornerRadius = new CornerRadius(6) };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_stripLabel, 1);
        Grid.SetRow(_stripArea, 2);
        Grid.SetRowSpan(_overlay, 3);
        root.Children.Add(_canvas);
        root.Children.Add(_preview);
        root.Children.Add(_stripLabel);
        root.Children.Add(_stripArea);
        root.Children.Add(_overlay);
        Content = root;

        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            var action = _pendingClick;
            _pendingClick = null;
            action?.Invoke();
        };
    }

    public event Action<DisplayId>? RenameRequested;

    private sealed class Press
    {
        public required ImmutableArray<DisplayId> Subject { get; init; }
        public required bool MemberOnly { get; init; }
        public required bool FromStrip { get; init; }
        public required Point Start { get; init; }
        public required Size GhostSize { get; init; }
        public required Vector Grab { get; init; }
        public required UIElement Source { get; init; }
        public Border? Ghost { get; set; }
        public DropPlan? Plan { get; set; }
        public bool Dragging => Ghost is not null;
    }

    public void Render(AppController controller)
    {
        _controller = controller;
        if (_press is not null) return; // never pull the floor out from under a drag
        _tiles.Clear();
        _canvas.Children.Clear();
        _strip.Children.Clear();

        var layout = controller.Draft;
        if (!layout.Items.IsEmpty)
        {
            _minX = layout.Items.Min(i => i.Bounds.X);
            _minY = layout.Items.Min(i => i.Bounds.Y);
            int width = layout.Items.Max(i => i.Bounds.Right) - _minX;
            int height = layout.Items.Max(i => i.Bounds.Bottom) - _minY;
            double available = double.IsNaN(Width) ? ActualWidth : Width;
            // Leave room for every connected display side by side, so drag previews of a new
            // position are never clipped and the scale stays put as displays are switched on and off.
            int everyDisplayInARow = layout.Items.Sum(i => i.Bounds.Width) + controller.Snapshot.Connected
                .Where(d => !layout.IsEnabled(d.Id))
                .Sum(d => d.PreferredSize.Width);
            _scale = Math.Min(
                Math.Min((available - 2 * Inset) / width, (MapHeight - 2 * Inset) / height),
                (available - 2 * Inset) / Math.Max(width, everyDisplayInARow));
            _offsetX = (available - width * _scale) / 2;
            _offsetY = (MapHeight - height * _scale) / 2;

            foreach (var item in layout.Items)
            {
                var rect = ToCanvas(item.Bounds);
                var tile = MakeTile(item.Members, item.IsPrimary, enabled: true, rect.Width - 3, rect.Height - 3);
                Canvas.SetLeft(tile, rect.X + 1.5);
                Canvas.SetTop(tile, rect.Y + 1.5);
                _canvas.Children.Add(tile);
                _tiles.Add((rect, item));
            }
        }

        var disabled = controller.Snapshot.Connected
            .Where(d => !layout.IsEnabled(d.Id))
            .OrderBy(d => controller.DisplayNumber(d.Id));
        foreach (var display in disabled)
        {
            var size = display.PreferredSize;
            double w = Math.Max(96, StripTileHeight * size.Width / size.Height);
            var tile = MakeTile([display.Id], isPrimary: false, enabled: false, w, StripTileHeight);
            tile.Margin = new Thickness(0, 0, 6, 6);
            _strip.Children.Add(tile);
        }
        _stripLabelText = _strip.Children.Count > 0 ? Loc.Get("Disabled") : Loc.Get("NothingDisabled");
        _stripLabel.Text = _stripLabelText;
    }

    // ---------- tiles ----------

    private Border MakeTile(ImmutableArray<DisplayId> members, bool isPrimary, bool enabled, double width, double height)
    {
        var c = _controller!;
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        foreach (var id in members)
        {
            var label = new TextBlock
            {
                Text = c.DisplayLabel(id),
                FontSize = enabled ? 12 : 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontStyle = c.Settings.IsUnrecognized(id) ? FontStyles.Italic : FontStyles.Normal,
                ToolTip = c.Settings.IsUnrecognized(id) ? Loc.Get("Unrecognized") : null,
            };
            label.SetResourceReference(StyleProperty, "Body");
            if (members.Length > 1)
            {
                // Inside a Mirror Group each name is its own click / drag target (decision Q19a).
                label.Tag = id;
                label.Cursor = Cursors.Hand;
                label.MouseEnter += (_, _) => label.TextDecorations = TextDecorations.Underline;
                label.MouseLeave += (_, _) => label.TextDecorations = null;
            }
            names.Children.Add(label);
        }

        var number = new TextBlock
        {
            Text = string.Join("·", members.Select(c.DisplayNumber)),
            FontSize = 10,
            Margin = new Thickness(6, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        number.SetResourceReference(StyleProperty, "Subtle");

        var content = new Grid();
        content.Children.Add(names);
        content.Children.Add(number);
        if (isPrimary)
        {
            var star = new TextBlock
            {
                Text = "\uE735",
                FontSize = 11,
                Margin = new Thickness(0, 4, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = Loc.Get("Primary"),
            };
            star.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            star.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            content.Children.Add(star);
        }

        var tile = new Border
        {
            Width = Math.Max(24, width),
            Height = Math.Max(20, height),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(isPrimary ? 2 : 1),
            Child = content,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };
        string background = enabled ? "Tile" : "TileOff";
        tile.SetResourceReference(Border.BackgroundProperty, background);
        tile.SetResourceReference(Border.BorderBrushProperty, isPrimary ? "Accent" : "TileBorder");
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "TileHover");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, background);
        tile.MouseLeftButtonDown += (_, e) => OnTileDown(e, tile, members, enabled);
        tile.MouseRightButtonUp += (_, e) =>
        {
            ShowTileMenu(members);
            e.Handled = true;
        };
        return tile;
    }

    // ---------- clicks and drags ----------

    private void OnTileDown(MouseButtonEventArgs e, Border tile, ImmutableArray<DisplayId> members, bool enabled)
    {
        var member = (e.OriginalSource as FrameworkElement)?.Tag as DisplayId?;
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            // Double click: make Primary (enabling first if needed). Cancels the pending single click.
            _clickTimer.Stop();
            _pendingClick = null;
            var id = member ?? members[0];
            _controller!.Edit((editor, draft) => editor.SetPrimary(draft, id));
            return;
        }

        bool memberOnly = member is not null && members.Length > 1;
        var subject = memberOnly ? [member!.Value] : members;
        bool wholeTile = enabled && !memberOnly;
        var ghost = wholeTile ? new Size(tile.Width, tile.Height) : ScaledPreferredSize(subject[0]);
        var grab = wholeTile ? (Vector)e.GetPosition(tile) : new Vector(ghost.Width / 2, ghost.Height / 2);

        _press = new Press
        {
            Subject = subject,
            MemberOnly = memberOnly,
            FromStrip = !enabled,
            Start = e.GetPosition(this),
            GhostSize = ghost,
            Grab = grab,
            Source = tile,
        };
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_press is null) return;
        var p = e.GetPosition(this);
        if (!_press.Dragging)
        {
            if ((p - _press.Start).Length < DragThreshold) return;
            _press.Ghost = new Border
            {
                Width = _press.GhostSize.Width,
                Height = _press.GhostSize.Height,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                Opacity = 0.85,
                Child = new TextBlock
                {
                    Text = string.Join(" + ", _press.Subject.Select(_controller!.DisplayLabel)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Style = (Style)FindResource("Body"),
                },
            };
            _press.Ghost.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            _press.Ghost.SetResourceReference(Border.BorderBrushProperty, "Accent");
            _overlay.Children.Add(_press.Ghost);
            _press.Source.Opacity = 0.35;
        }

        var ghostTopLeft = new Point(p.X - _press.Grab.X, p.Y - _press.Grab.Y);
        _press.Plan = Evaluate(_press, p, ghostTopLeft);
        var shown = ShowPreview(_press, _press.Plan) is { } landing && (landing - ghostTopLeft).Length <= MagnetRadius
            ? landing // weak magnet: close to where it will land, the ghost settles there
            : ghostTopLeft;
        Canvas.SetLeft(_press.Ghost!, shown.X);
        Canvas.SetTop(_press.Ghost!, shown.Y);
        // Over another tile the ghost would hide the highlighted target and its edge bar.
        _press.Ghost!.Opacity = _press.Plan is DropPlan.OntoTile ? 0.35 : 0.85;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_press is null) return;
        var press = _press;
        _press = null;
        ReleaseMouseCapture();

        if (press.Dragging)
        {
            EndDrag(press);
            Execute(press.Plan);
            return;
        }

        // Single click waits out the double-click time so a double click never toggles first.
        _pendingClick = () => Click(press);
        _clickTimer.Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime);
        _clickTimer.Start();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_press is not { Dragging: true } press) return;
        _press = null;
        EndDrag(press);
    }

    private void Click(Press press)
    {
        var id = press.Subject[0];
        if (press.FromStrip) _controller!.Edit((editor, draft) => editor.Enable(draft, id));
        else if (press.MemberOnly) _controller!.Edit((editor, draft) => editor.DisableDisplay(draft, id));
        else _controller!.Edit((editor, draft) => editor.DisableItem(draft, id));
    }

    // ---------- drop plans: what releasing here would do ----------

    /// <summary>
    /// The outcome of releasing at the current point. Computed with the same editor operation that
    /// the release executes, so the preview is exactly the result.
    /// </summary>
    private abstract record DropPlan
    {
        public sealed record Nothing : DropPlan;
        public sealed record Disable(EditResult Result) : DropPlan;
        public sealed record Move(EditResult Result) : DropPlan;
        public sealed record OntoTile(ImmutableArray<DisplayId> Subject, DisplayId Target, Rect TargetRect, Side Side) : DropPlan;
    }

    private DropPlan Evaluate(Press press, Point p, Point ghostTopLeft)
    {
        var c = _controller!;
        var subject = press.Subject;

        if (StripBounds().Contains(p))
        {
            if (press.FromStrip) return new DropPlan.Nothing();
            var id = subject[0];
            return new DropPlan.Disable(press.MemberOnly
                ? c.Editor.DisableDisplay(c.Draft, id)
                : c.Editor.DisableItem(c.Draft, id));
        }

        var inCanvas = TranslatePoint(p, _canvas);
        if (inCanvas.X < 0 || inCanvas.Y < 0 || inCanvas.X > _canvas.ActualWidth || inCanvas.Y > _canvas.ActualHeight)
            return new DropPlan.Nothing();

        var target = _tiles.FirstOrDefault(t => t.Rect.Contains(inCanvas) && !t.Item.Members.Any(subject.Contains));
        if (target.Item is not null)
            return new DropPlan.OntoTile(subject, target.Item.Members[0], target.Rect, SideOf(target.Rect, inCanvas));

        // Empty space: the editor snaps it to the nearest free edge.
        var topLeft = TranslatePoint(ghostTopLeft, _canvas);
        int x = (int)Math.Round((topLeft.X - _offsetX) / _scale) + _minX;
        int y = (int)Math.Round((topLeft.Y - _offsetY) / _scale) + _minY;
        return new DropPlan.Move(c.Editor.Move(c.Draft, subject, x, y));
    }

    private void Execute(DropPlan? plan)
    {
        var c = _controller!;
        switch (plan)
        {
            case DropPlan.Disable d:
                c.Edit((_, _) => d.Result);
                break;
            case DropPlan.Move m:
                c.Edit((_, _) => m.Result);
                break;
            case DropPlan.OntoTile t:
                ShowDropMenu(t.Subject, t.Target, t.Side);
                break;
        }
    }

    /// <summary>
    /// Draws what the release would do: the resulting Layout (Move / Disable), or the target tile with
    /// the edge "Extend" would attach to (OntoTile). Returns where the ghost would land, for the magnet.
    /// </summary>
    private Point? ShowPreview(Press press, DropPlan plan)
    {
        ClearPreview();
        switch (plan)
        {
            case DropPlan.Disable { Result.Error: { } error }:
                HighlightStrip(ok: false);
                ShowHint(Loc.Get(ErrorKey(error)), ok: false);
                return null;
            case DropPlan.Disable d:
                HighlightStrip(ok: true);
                ShowHint(Loc.Get("DropDisable"), ok: true);
                DrawLayout(d.Result.Layout, press.Subject);
                return null;
            case DropPlan.Move { Result.Error: { } error }:
                ShowHint(Loc.Get(ErrorKey(error)), ok: false);
                return null;
            case DropPlan.Move m:
                ShowHint(Loc.Get("DropMove"), ok: true);
                var landing = DrawLayout(m.Result.Layout, press.Subject);
                return landing is { } r ? _canvas.TranslatePoint(r.TopLeft, _overlay) : null;
            case DropPlan.OntoTile t:
                DrawTarget(t.TargetRect, t.Side);
                ShowHint(Loc.Format("DropOnTile", Loc.Get("Side" + t.Side)), ok: true);
                return null;
            default:
                return null;
        }
    }

    /// <summary>Shows a would-be Layout in place of the real tiles; the dragged display is highlighted.</summary>
    private Rect? DrawLayout(DisplayLayout result, ImmutableArray<DisplayId> subject)
    {
        var (dx, dy) = FrameShift(result, subject);
        Rect? landing = null;
        _canvas.Opacity = 0;
        foreach (var item in result.Items)
        {
            var rect = ToCanvas(item.Bounds.Offset(dx, dy));
            var tile = MakeTile(item.Members, item.IsPrimary, enabled: true, rect.Width - 3, rect.Height - 3);
            if (item.Members.Any(subject.Contains))
            {
                tile.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                tile.SetResourceReference(Border.BorderBrushProperty, "Accent");
                tile.BorderThickness = new Thickness(2);
                landing = rect;
            }
            Canvas.SetLeft(tile, rect.X + 1.5);
            Canvas.SetTop(tile, rect.Y + 1.5);
            _preview.Children.Add(tile);
        }
        return landing;
    }

    /// <summary>The target tile lights up, with a bar on the edge "Extend" would attach to.</summary>
    private void DrawTarget(Rect target, Side side)
    {
        var frame = new Border
        {
            Width = target.Width - 3,
            Height = target.Height - 3,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
        };
        frame.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        Canvas.SetLeft(frame, target.X + 1.5);
        Canvas.SetTop(frame, target.Y + 1.5);
        _preview.Children.Add(frame);

        const double bar = 5;
        var edge = new Border { CornerRadius = new CornerRadius(2) };
        edge.SetResourceReference(Border.BackgroundProperty, "Accent");
        bool vertical = side is Side.Left or Side.Right;
        edge.Width = vertical ? bar : target.Width - 12;
        edge.Height = vertical ? target.Height - 12 : bar;
        Canvas.SetLeft(edge, side switch { Side.Left => target.X, Side.Right => target.Right - bar, _ => target.X + 6 });
        Canvas.SetTop(edge, side switch { Side.Top => target.Y, Side.Bottom => target.Bottom - bar, _ => target.Y + 6 });
        _preview.Children.Add(edge);
    }

    /// <summary>
    /// Relayout keeps the Primary at the origin, so a result can be shifted relative to the drawn frame.
    /// Pin it to an item that did not move: the Primary if it was not dragged, else any untouched item.
    /// </summary>
    private (int Dx, int Dy) FrameShift(DisplayLayout result, ImmutableArray<DisplayId> subject)
    {
        var draft = _controller!.Draft;
        var anchors = draft.Items
            .Where(i => !i.Members.Any(subject.Contains))
            .OrderByDescending(i => i.IsPrimary);
        foreach (var before in anchors)
        {
            var after = result.ItemOf(before.Members[0]);
            if (after is null) continue;
            return (before.Bounds.X - after.Bounds.X, before.Bounds.Y - after.Bounds.Y);
        }
        return (0, 0);
    }

    private void HighlightStrip(bool ok)
    {
        _stripArea.SetResourceReference(Border.BackgroundProperty, ok ? "AccentSoft" : "TileOff");
        _stripArea.SetResourceReference(Border.BorderBrushProperty, ok ? "Accent" : "Warning");
        _stripArea.BorderThickness = new Thickness(1);
    }

    private void ShowHint(string text, bool ok)
    {
        _stripLabel.Text = text;
        _stripLabel.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Accent" : "Warning");
    }

    private void ClearPreview()
    {
        _preview.Children.Clear();
        _canvas.Opacity = 1;
        _stripArea.Background = Brushes.Transparent;
        _stripArea.BorderThickness = new Thickness(0);
        _stripLabel.ClearValue(TextBlock.ForegroundProperty);
        _stripLabel.Text = _stripLabelText;
    }

    private void EndDrag(Press press)
    {
        _overlay.Children.Clear();
        press.Source.Opacity = 1;
        ClearPreview();
    }

    private Rect StripBounds()
    {
        var bounds = _stripArea.TransformToAncestor(this).TransformBounds(new Rect(_stripArea.RenderSize));
        bounds.Inflate(0, 8);
        return bounds;
    }

    private static string ErrorKey(EditError error) =>
        error == EditError.LastActiveDisplay ? "LastActive" : "DifferentAdapters";

    /// <summary>Dropped on another tile: "Duplicate" or "Extend" towards the side it was dropped on (decision Q2/Q9).</summary>
    private void ShowDropMenu(ImmutableArray<DisplayId> subject, DisplayId target, Side side)
    {
        var c = _controller!;
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(MenuItem(Loc.Get("Duplicate"), () => c.Edit((editor, draft) => editor.Mirror(draft, subject, target))));
        menu.Items.Add(MenuItem(Loc.Get("Extend"), () => c.Edit((editor, draft) => editor.Extend(draft, subject, target, side))));
        menu.IsOpen = true;
    }

    private void ShowTileMenu(ImmutableArray<DisplayId> members)
    {
        var c = _controller!;
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var id in members)
        {
            string header = members.Length == 1 ? Loc.Get("Rename") : Loc.Format("RenameNamed", c.DisplayLabel(id));
            menu.Items.Add(MenuItem(header, () => RenameRequested?.Invoke(id)));
        }
        if (members.Length > 1)
            menu.Items.Add(MenuItem(Loc.Get("Ungroup"), () => c.Edit((editor, draft) => editor.Ungroup(draft, members[0]))));

        // "This is …": an Unrecognized Display takes over a disconnected named one (ADR-0002).
        var candidates = c.DisconnectedNamedDisplays().ToList();
        foreach (var id in members.Where(c.Settings.IsUnrecognized))
        foreach (var (oldId, name) in candidates)
            menu.Items.Add(MenuItem(Loc.Format("ThisIs", name), () => c.RemapDisplay(oldId, id)));

        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // ---------- geometry ----------

    private Rect ToCanvas(PixelRect r) => new(
        _offsetX + (r.X - _minX) * _scale,
        _offsetY + (r.Y - _minY) * _scale,
        r.Width * _scale,
        r.Height * _scale);

    private Size ScaledPreferredSize(DisplayId id)
    {
        var size = _controller!.Snapshot.Connected.First(d => d.Id == id).PreferredSize;
        double scale = _scale > 0 ? _scale : StripTileHeight / size.Height;
        return new Size(size.Width * scale, size.Height * scale);
    }

    private static Side SideOf(Rect target, Point p)
    {
        double dx = (p.X - (target.X + target.Width / 2)) / target.Width;
        double dy = (p.Y - (target.Y + target.Height / 2)) / target.Height;
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx < 0 ? Side.Left : Side.Right;
        return dy < 0 ? Side.Top : Side.Bottom;
    }
}
