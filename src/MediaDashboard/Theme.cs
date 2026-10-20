using System.Windows;
using System.Windows.Media;
using Windows.UI.ViewManagement;

namespace MediaDashboard;

/// <summary>Light/dark palette following Windows (decision Q26), published as DynamicResource brushes.</summary>
public static class Theme
{
    private static readonly UISettings UiSettings = new();

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        Apply();
        // Fires on a worker thread for theme and accent changes.
        UiSettings.ColorValuesChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Apply();
            Changed?.Invoke();
        });
    }

    private static void Apply()
    {
        IsDark = Native.AppsUseDarkTheme();
        var accentValue = UiSettings.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        var accent = Color.FromRgb(accentValue.R, accentValue.G, accentValue.B);

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
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
