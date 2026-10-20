using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MediaDashboard.Core;
using Microsoft.Win32;

namespace MediaDashboard;

/// <summary>Win32 helpers for window chrome and placement in physical desktop pixels.</summary>
internal static partial class Native
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    public static bool AppsUseDarkTheme() => ReadPersonalize("AppsUseLightTheme") == 0;

    public static bool TaskbarUsesDarkTheme() => ReadPersonalize("SystemUsesLightTheme") == 0;

    private static int ReadPersonalize(string value) =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", value, 1) as int? ?? 1;

    /// <summary>Win11 acrylic backdrop, rounded corners and dark caption to match the system theme.</summary>
    public static void ApplyBackdrop(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(hwnd) is { } source) source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        int darkMode = dark ? 1 : 0, corner = DWMWCP_ROUND, backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    /// <summary>Keeps the window out of Alt+Tab; optionally never activated and click-through (on-screen plates).</summary>
    public static void SetToolWindow(Window window, bool passive)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (passive) style |= WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)style);
    }

    /// <summary>Physical size of a shown window.</summary>
    public static (int Width, int Height) PhysicalSize(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void MoveTo(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
    }

    /// <summary>Centers a shown window on a desktop rectangle given in physical pixels.</summary>
    public static void CenterOn(Window window, PixelRect area)
    {
        // Move first so per-monitor DPI scaling settles, then center using the final size.
        MoveTo(window, area.X + area.Width / 2, area.Y + area.Height / 2);
        var (w, h) = PhysicalSize(window);
        MoveTo(window, area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2);
    }
}
