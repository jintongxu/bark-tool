using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace BarkTool.Desktop;

public partial class RadialMenuWindow : Window
{
    private readonly App _app = (App)Application.Current;

    public RadialMenuWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Closing += (_, e) => { if (!_app.IsExiting) { e.Cancel = true; Hide(); } };
    }

    internal void OpenAt(FloatWindow anchor)
    {
        var center = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(anchor).Handle).WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(anchor);
        var width = (int)Math.Round(Width * dpi.DpiScaleX);
        var height = (int)Math.Round(Height * dpi.DpiScaleY);
        var x = (int)Math.Clamp(center.X - width / 2d, monitor.Left, Math.Max(monitor.Left, monitor.Right - width));
        var y = (int)Math.Clamp(center.Y - height / 2d, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - height));
        // Move in physical coordinates to avoid mixed-DPI virtual-screen origin errors.
        SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010);
        HoverLabel.Text = "选择操作";
        Show();
        Activate();
    }

    private void Action_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button) HoverLabel.Text = button.ToolTip?.ToString() ?? "选择操作";
    }

    private void Action_MouseLeave(object sender, MouseEventArgs e) => HoverLabel.Text = "选择操作";

    private async void Capture_Click(object sender, RoutedEventArgs e) { Hide(); await _app.CaptureAsync(); }

    private void Compose_Click(object sender, RoutedEventArgs e) { Hide(); _app.OpenSend(); }
    private async void Clipboard_Click(object sender, RoutedEventArgs e) { Hide(); await _app.SendClipboardAsync(); }
    private void Image_Click(object sender, RoutedEventArgs e) { Hide(); _app.OpenImage(); }
    private void Settings_Click(object sender, RoutedEventArgs e) { Hide(); _app.OpenSettings(); }
    private void Collapse_Click(object sender, RoutedEventArgs e) => Hide();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
