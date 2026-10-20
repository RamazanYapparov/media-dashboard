using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MediaDashboard.Core;

namespace MediaDashboard;

/// <summary>
/// "Keep these display settings?" with a 10 s countdown after applying a Draft (decision Q16).
/// Shown on the new Primary display; anything but "Keep" rolls back.
/// </summary>
public sealed class CountdownWindow : Window
{
    private const int Seconds = 10;
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _countdown;
    private int _remaining = Seconds;

    private CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome { GlassFrameThickness = new Thickness(-1), CaptionHeight = 0, ResizeBorderThickness = new Thickness(0) });

        var title = new TextBlock { Text = Loc.Get("KeepChanges"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        title.SetResourceReference(StyleProperty, "Body");
        _countdown = new TextBlock { Margin = new Thickness(0, 0, 0, 18) };
        _countdown.SetResourceReference(StyleProperty, "Subtle");

        var keep = new Button { Content = Loc.Get("Keep"), Tag = "Accent", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        var revert = new Button { Content = Loc.Get("Revert"), IsCancel = true, MinWidth = 110 };
        keep.SetResourceReference(StyleProperty, "FlatButton");
        revert.SetResourceReference(StyleProperty, "FlatButton");
        keep.Click += (_, _) => Finish(true);
        revert.Click += (_, _) => Finish(false);

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children =
            {
                title,
                _countdown,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { keep, revert } },
            },
        };

        SourceInitialized += (_, _) =>
        {
            Native.SetToolWindow(this, passive: false);
            Native.ApplyBackdrop(this, Theme.IsDark);
        };
        Closed += (_, _) => _result.TrySetResult(false);
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            UpdateText();
            if (_remaining <= 0) Finish(false);
        };
        UpdateText();
    }

    public static Task<bool> AskAsync(PixelRect primaryBounds)
    {
        var window = new CountdownWindow();
        window.Show();
        Native.CenterOn(window, primaryBounds);
        window.Activate();
        window._timer.Start();
        return window._result.Task;
    }

    private void UpdateText() => _countdown.Text = Loc.Format("RevertIn", _remaining);

    private void Finish(bool keep)
    {
        _timer.Stop();
        _result.TrySetResult(keep);
        Close();
    }
}

/// <summary>Never-focused, click-through plate centered on a desktop area that closes itself.</summary>
public abstract class PassiveOverlay : Window
{
    protected PassiveOverlay(UIElement content, TimeSpan lifetime)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        var plate = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(28, 18, 28, 18), Child = content };
        plate.SetResourceReference(Border.BackgroundProperty, "Surface");
        plate.SetResourceReference(Border.BorderBrushProperty, "TileBorder");
        plate.BorderThickness = new Thickness(1);
        Content = plate;

        SourceInitialized += (_, _) => Native.SetToolWindow(this, passive: true);
        var timer = new DispatcherTimer { Interval = lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        Loaded += (_, _) => timer.Start();
    }

    protected void ShowOn(PixelRect area)
    {
        Show();
        Native.CenterOn(this, area);
    }
}

/// <summary>Warning plate after applying a Preset from the tray when something was missing (decision Q33).</summary>
public sealed class OsdWindow : PassiveOverlay
{
    private OsdWindow(string text) : base(Body(text), TimeSpan.FromSeconds(2.5)) { }

    public static void Show(string text, PixelRect primaryBounds) => new OsdWindow(text).ShowOn(primaryBounds);

    private static UIElement Body(string text)
    {
        var icon = new TextBlock { Text = "\uE7BA", FontSize = 22, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        var message = new TextBlock { Text = text, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        message.SetResourceReference(StyleProperty, "Body");
        message.FontSize = 15;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, message } };
    }
}

/// <summary>"Identify": the same number as on the tile, shown on every physical screen for 3 s (decision Q22).</summary>
public sealed class IdentifyWindow : PassiveOverlay
{
    private IdentifyWindow(string number, string label) : base(Body(number, label), TimeSpan.FromSeconds(3)) { }

    public static void ShowAll(AppController controller)
    {
        // A Mirror Group shows one picture on all members, so it gets one plate listing all of them.
        foreach (var item in controller.Snapshot.Layout.Items)
        {
            string number = string.Join(" · ", item.Members.Select(controller.DisplayNumber));
            string label = string.Join(" + ", item.Members.Select(controller.DisplayLabel));
            new IdentifyWindow(number, label).ShowOn(item.Bounds);
        }
    }

    private static UIElement Body(string number, string label)
    {
        var big = new TextBlock { Text = number, FontSize = 96, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        big.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var name = new TextBlock { Text = label, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
        name.SetResourceReference(StyleProperty, "Body");
        name.FontSize = 16;
        return new StackPanel { MinWidth = 180, Children = { big, name } };
    }
}
