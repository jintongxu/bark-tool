using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace BarkTool.Desktop;

internal static class FullScreenDetector
{
    internal static bool IsFullScreenOnMonitor(IntPtr floatingHandle)
    {
        if (floatingHandle == IntPtr.Zero || !IsWindow(floatingHandle))
            return false;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == GetShellWindow()
            || !IsWindowVisible(foreground) || IsIconic(foreground))
            return false;
        if (GetWindowThreadProcessId(foreground, out var processId) == 0
            || processId == (uint)Environment.ProcessId)
            return false;
        var className = new StringBuilder(256);
        if (GetClassName(foreground, className, className.Capacity) == 0)
            return false;
        if (className.ToString() is "Progman" or "WorkerW")
            return false;

        // Do not map invalid/off-screen handles to an arbitrary nearest monitor.
        var monitor = MonitorFromWindow(foreground, 0);
        if (monitor == IntPtr.Zero || MonitorFromWindow(floatingHandle, 0) != monitor)
            return false;
        // Windows reports exclusive Direct3D fullscreen even when DWM cannot read its frame.
        if (SHQueryUserNotificationState(out var state) == 0 && state == 3)
            return foreground == GetForegroundWindow();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)
            || DwmGetWindowAttribute(foreground, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */,
                out var bounds, Marshal.SizeOf<NativeRect>()) != 0)
            return false;
        // Recheck focus because the foreground window can disappear during probing.
        return foreground == GetForegroundWindow()
            && (CoversMonitor(bounds.ToRectangle(), info.Monitor.ToRectangle())
                || CoversWithFrame(bounds.ToRectangle(), info.Monitor.ToRectangle()));
    }

    internal static bool CoversMonitor(Rectangle window, Rectangle monitor)
    {
        if (window.Width <= 0 || window.Height <= 0 || monitor.Width <= 0 || monitor.Height <= 0)
            return false;
        // Compare each edge, not just area: a maximized window leaves taskbar space.
        return Math.Abs((long)window.Left - monitor.Left) <= 2
            && Math.Abs((long)window.Top - monitor.Top) <= 2
            && Math.Abs((long)window.X + window.Width - ((long)monitor.X + monitor.Width)) <= 2
            && Math.Abs((long)window.Y + window.Height - ((long)monitor.Y + monitor.Height)) <= 2;
    }

    internal static bool CoversWithFrame(Rectangle window, Rectangle monitor)
    {
        // Some game windows retain an invisible resize frame outside the full screen.
        return monitor.Width > 0 && monitor.Height > 0
            && window.Left <= monitor.Left && window.Top <= monitor.Top
            && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom
            && (long)monitor.Left - window.Left <= 16 && (long)monitor.Top - window.Top <= 16
            && (long)window.Right - monitor.Right <= 16 && (long)window.Bottom - monitor.Bottom <= 16;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute,
        out NativeRect value, int size);
}
