using System.IO;
using Microsoft.Win32;

namespace AIUsageMonitor.Services;

public sealed class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIUsageMonitor";

    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        string? registeredCommand = key?.GetValue(ValueName) as string;
        return string.Equals(registeredCommand, GetStartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using RegistryKey? existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using RegistryKey startupKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");
        startupKey.SetValue(ValueName, GetStartupCommand(), RegistryValueKind.String);
    }

    private static string GetStartupCommand()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.GetFileName(executablePath).Equals("AIUsageMonitor.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Windows startup can only be enabled from AIUsageMonitor.exe.");
        }

        return $"\"{Path.GetFullPath(executablePath)}\"";
    }
}
