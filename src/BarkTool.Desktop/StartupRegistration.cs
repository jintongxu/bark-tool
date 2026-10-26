using Microsoft.Win32;

namespace BarkTool.Desktop;

internal static class StartupRegistration
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BarkTool";

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序位置。");
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, true);
            Write(key, true, path);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, true);
            if (key is not null) Write(key, false, "");
        }
    }

    internal static void Write(RegistryKey key, bool enabled, string executablePath)
    {
        if (enabled)
        {
            if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"')
                || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(executablePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请从发布版 BarkTool.exe 设置开机自启。");
            key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, false);
    }
}
