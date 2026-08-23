using System;
using Microsoft.Win32;

namespace Peggy.Tray;

public static class StartupManager
{
    private const string RunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Peggy";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RunKey,
            writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The assistant could not determine its executable path.");
        key.SetValue(
            ValueName,
            $"\"{executable}\"",
            RegistryValueKind.String);
    }
}
