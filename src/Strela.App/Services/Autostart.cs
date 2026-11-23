using Microsoft.Win32;

namespace Strela.App.Services;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Strela";

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, "\"" + exePath + "\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
