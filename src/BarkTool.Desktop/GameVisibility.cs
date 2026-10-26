using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BarkTool.Desktop;

internal static class GameVisibility
{
    internal static string[] Parse(string text) => text.Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => Path.GetFileNameWithoutExtension(value)).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static bool Matches(string name, string configured) => Parse(configured).Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);

    internal static bool IsForegroundGame(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return Matches(process.ProcessName, configured);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Foreground process may exit, or Windows may deny querying its name.
            return false;
        }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
