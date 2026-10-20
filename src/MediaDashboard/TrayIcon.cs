using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MediaDashboard.Core;
using MediaDashboard.Platform;

namespace MediaDashboard;

/// <summary>
/// Tray icon (decision Q8/Q31): shows the Active Audio Output's kind, tooltip names the Matching Preset.
/// Left click toggles the flyout; right click opens presets / Open / Language / Autostart / Exit.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private readonly AppController _controller;
    private readonly Action _toggleFlyout;
    private readonly Action _exit;
    private readonly NotifyIcon _icon;
    private IntPtr _hIcon;
    private string? _renderedKey;

    public TrayIcon(AppController controller, Action toggleFlyout, Action exit)
    {
        _controller = controller;
        _toggleFlyout = toggleFlyout;
        _exit = exit;

        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Opening += (_, _) => BuildMenu(menu);
        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _toggleFlyout();
        };

        _controller.StateChanged += Update;
        Theme.Changed += () =>
        {
            _renderedKey = null;
            Update();
        };
        Loc.Instance.PropertyChanged += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        var kind = _controller.ActiveAudio?.Kind;
        bool dark = Native.TaskbarUsesDarkTheme();
        string key = $"{kind}|{dark}";
        if (key != _renderedKey)
        {
            var previous = _hIcon;
            _hIcon = RenderGlyph(Glyph(kind), dark ? Color.White : Color.Black);
            _icon.Icon = Icon.FromHandle(_hIcon);
            if (previous != IntPtr.Zero) DestroyIcon(previous);
            _renderedKey = key;
        }

        var text = _controller.MatchingPreset?.Name ?? Loc.Get("CustomConfig");
        if (_controller.ActiveAudio is { } audio) text += Environment.NewLine + _controller.AudioLabel(audio);
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Renderer = Theme.IsDark ? new ToolStripProfessionalRenderer(new DarkColors()) : new ToolStripProfessionalRenderer();
        menu.ForeColor = Theme.IsDark ? Color.White : Color.Black;

        var matching = _controller.MatchingPreset;
        foreach (var preset in _controller.Settings.Presets)
        {
            var item = new ToolStripMenuItem(preset.Name) { Checked = ReferenceEquals(preset, matching) };
            item.Click += (_, _) => _controller.ApplyPreset(preset, fromTray: true);
            menu.Items.Add(item);
        }
        if (_controller.Settings.Presets.Count > 0) menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Item(Loc.Get("Open"), _toggleFlyout));

        var language = new ToolStripMenuItem(Loc.Get("Language"));
        foreach (var (choice, label) in new[]
                 {
                     (LanguageChoice.Auto, Loc.Get("LangAuto")),
                     (LanguageChoice.Russian, "Русский"),
                     (LanguageChoice.English, "English"),
                 })
        {
            var option = Item(label, () => _controller.SetLanguage(choice));
            option.Checked = _controller.Settings.Language == choice;
            option.ForeColor = menu.ForeColor;
            language.DropDownItems.Add(option);
        }
        if (Theme.IsDark) language.DropDown.BackColor = DarkColors.Background;
        menu.Items.Add(language);

        var autostart = Item(Loc.Get("Autostart"), () => Autostart.Set(!Autostart.IsEnabled));
        autostart.Checked = Autostart.IsEnabled;
        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item(Loc.Get("Exit"), _exit));

        foreach (ToolStripItem item in menu.Items) item.ForeColor = menu.ForeColor;
    }

    private static ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Segoe Fluent Icons glyph for the Active Audio Output's kind.</summary>
    internal static string Glyph(AudioOutputKind? kind) => kind switch
    {
        AudioOutputKind.Headphones => "\uE7F6",
        AudioOutputKind.Headset => "\uE95B",
        AudioOutputKind.Speakers => "\uE7F5",
        AudioOutputKind.DigitalOutput => "\uE7F5",
        AudioOutputKind.DisplayAudio => "\uE7F4",
        AudioOutputKind.Other => "\uE767",
        null => "\uE74F",
        _ => "\uE767",
    };

    private static IntPtr RenderGlyph(string glyph, Color color)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe Fluent Icons", size * 0.8f, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        }
        return bitmap.GetHicon();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    private sealed class DarkColors : ProfessionalColorTable
    {
        public static readonly Color Background = Color.FromArgb(0x2B, 0x2B, 0x2B);
        private static readonly Color Hover = Color.FromArgb(0x3D, 0x3D, 0x3D);
        private static readonly Color Border = Color.FromArgb(0x45, 0x45, 0x45);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
