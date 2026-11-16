using System;
using Microsoft.Win32;

namespace Waypoint.App;

public static class StartupSetting
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Waypoint";

    /// <summary>Passed when Windows starts Waypoint at sign-in, so it can leave background work until things settle.</summary>
    public const string AtLoginArgument = "--at-login";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
    }

    /// <summary>Keeps the sign-in entry pointing at this copy, with the argument, when it's turned on.</summary>
    public static void Refresh()
    {
        if (IsEnabled)
        {
            Set(true);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {AtLoginArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
