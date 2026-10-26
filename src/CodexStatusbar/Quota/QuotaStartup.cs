using Microsoft.Win32;

namespace CodexStatusbar.Quota;

internal static class QuotaStartup
{
    internal const string ValueName = "CodexQuotaBattery";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string BuildCommand(string exe) => "\"" + Path.GetFullPath(exe) + "\" --watch-codex";
    internal static bool IsOurCommand(string? command, string exe) =>
        string.Equals(command?.Trim(), BuildCommand(exe), StringComparison.OrdinalIgnoreCase);
    internal static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }
    internal static void RemoveLegacyStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
