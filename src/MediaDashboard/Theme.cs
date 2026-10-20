using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MediaDashboard;

/// <summary>Light/dark palette following Windows (decision Q26), published as DynamicResource brushes.</summary>
public static class Theme
{
    private const string AccentKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    // AccentPalette holds 8 RGBA entries: Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, (unused).
    // Same values as WinRT UISettings.GetColorValue, without pulling the 25 MB Windows SDK projection into the exe.
    private const int AccentLight2 = 1, AccentDark1 = 4;

    private static string _applied = "";

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        Apply();
        // Raised on a SystemEvents thread for theme, accent and colorization changes.
        SystemEvents.UserPreferenceChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (Apply()) Changed?.Invoke();
        });
    }

    /// <summary>Publishes the palette; returns false when nothing changed.</summary>
    private static bool Apply()
    {
        bool dark = Native.AppsUseDarkTheme();
        var accent = ReadAccent(dark ? AccentLight2 : AccentDark1);
        string key = $"{dark}|{accent}";
        if (key == _applied) return false;
        _applied = key;
        IsDark = dark;

        var r = Application.Current.Resources;
        r["Text"] = Brush(IsDark ? "#FFFFFF" : "#1A1A1A");
        r["TextSubtle"] = Brush(IsDark ? "#B3FFFFFF" : "#8A000000");
        r["Tile"] = Brush(IsDark ? "#1AFFFFFF" : "#B3FFFFFF");
        r["TileHover"] = Brush(IsDark ? "#26FFFFFF" : "#E6FFFFFF");
        r["TileOff"] = Brush(IsDark ? "#0AFFFFFF" : "#40FFFFFF");
        r["TileBorder"] = Brush(IsDark ? "#1FFFFFFF" : "#1A000000");
        r["Divider"] = Brush(IsDark ? "#15FFFFFF" : "#12000000");
        r["Surface"] = Brush(IsDark ? "#F0202020" : "#F5F9F9F9");
        r["Accent"] = new SolidColorBrush(accent);
        r["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        r["OnAccent"] = Brush(IsDark ? "#000000" : "#FFFFFF");
        r["Warning"] = Brush(IsDark ? "#FCE100" : "#9D5D00");
        return true;
    }

    private static Color ReadAccent(int index)
    {
        if (Registry.GetValue(AccentKey, "AccentPalette", null) is byte[] palette && palette.Length >= (index + 1) * 4)
            return Color.FromRgb(palette[index * 4], palette[index * 4 + 1], palette[index * 4 + 2]);
        return index == AccentLight2 ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0); // Windows default blue
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
