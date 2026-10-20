using Microsoft.Win32;

namespace MediaDashboard.Platform;

/// <summary>Start with Windows via the per-user Run key.</summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MediaDashboard";

    /// <summary>True when the Run entry exists and launches this executable.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command
                   && string.Equals(command, Command(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(ValueName, Command(), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string Command() =>
        $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.")}\"";
}
