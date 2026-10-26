using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BarkTool.Desktop;

public partial class FloatWindow : Window
{
    private Point _down;
    private bool _dragged;
    private bool _dragging;
    private bool _dropHover;
    private bool _idle;
    private readonly App _app = (App)Application.Current;
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private FloatingOptions _options = new();
    private FloatingBehavior.DockSide _dock = FloatingBehavior.DockSide.None;
    private bool _peeked;
    private int _anchorX;
    private int _anchorY;
    private bool _hasAnchor;
    private readonly ContextMenu _menu = new();

    public FloatWindow()
    {
        InitializeComponent();
        AddAction("写内容", _app.OpenSend);
        AddAction("框选截图", async () => await _app.CaptureAsync());
        _menu.Items.Add(new Separator());
        AddAction("隐藏悬浮按钮", () => _app.SetFloatingHidden(true));
        AddAction("设置", _app.OpenSettings);
        _menu.Items.Add(new Separator());
        AddAction("退出", _app.ExitApplication);
        Bubble.ContextMenu = _menu;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { _menu.IsOpen = false; } };
        _menu.Opened += (_, _) => RefreshInteractionState();
        _menu.Closed += (_, _) => RefreshInteractionState();
        MouseEnter += (_, _) => RefreshInteractionState();
        MouseLeave += (_, _) => RefreshInteractionState();
        _fadeTimer.Tick += (_, _) =>
        {
            _fadeTimer.Stop(); // One-shot idle deadline, never polling.
            _idle = true;
            UpdateOpacity();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _menu.IsOpen = false;
                _dropHover = false;
            }
            RefreshInteractionState();
        };
        Closed += (_, _) => _fadeTimer.Stop();
        SourceInitialized += (_, _) =>
        {
            ApplyOptions(_app.FloatingOptions);
            if (_app.Settings.FloatingLeft is double left && double.IsFinite(left)) Left = left;
            if (_app.Settings.FloatingTop is double top && double.IsFinite(top)) Top = top;
        };
        Loaded += (_, _) =>
        {
            // WPF has now applied its own DIP positioning and per-monitor DPI.
            // Only use native physical bounds from here; never divide desktop origins by DPI.
            var handle = new WindowInteropHelper(this).Handle;
            if (_app.Settings.FloatingLeft is null && _app.Settings.FloatingTop is null
                && GetWindowRect(handle, out var bounds))
            {
                var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                SetWindowPos(handle, IntPtr.Zero, work.Right - bounds.Width - 24,
                    work.Top + Math.Max(0, (work.Height - bounds.Height) / 2), 0, 0, 0x15);
            }
            ClampToScreen();
            _peeked = false;
            _hasAnchor = false;
            RefreshInteractionState();
        };
    }

    private void AddAction(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => { _app.CloseRadial(); action(); };
        _menu.Items.Add(item);
    }

    internal void ApplyOptions(FloatingOptions options)
    {
        var previous = _options;
        _options = options.Normalize();
        Width = Height = _options.Size;
        Bubble.CornerRadius = new CornerRadius((_options.Size - 6) / 2);
        if (!_options.Peek && previous.Peek) { _peeked = false; _hasAnchor = false; }
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(ClampToScreen), DispatcherPriority.Loaded);
        RefreshInteractionState();
    }

    // App calls this when radial visibility changes, including fullscreen/manual hide.
    internal void RefreshInteractionState()
    {
        _fadeTimer.Stop();
        _idle = false;
        UpdateOpacity();
        if (IsVisible && _options.Fade && !Interacting) _fadeTimer.Start();
    }

    private bool Interacting => IsMouseOver || _dragging || IsMouseCaptured || _dropHover
        || _menu.IsOpen || _app.IsRadialOpen;

    private void UpdateOpacity()
    {
        Opacity = FloatingBehavior.DisplayOpacity(_options, _idle, Interacting);
        UpdatePeek();
    }

    // Docked buttons retract to a narrow strip and slide back under the pointer.
    private void UpdatePeek()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !IsVisible || !GetWindowRect(handle, out var bounds)) return;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1 : dpi / 96d;
        var window = new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        // Recompute the dock anchor only while fully expanded; retracted bounds are
        // outside the work area and would otherwise be misread as undocked.
        if (!_peeked && !_dragging && !IsMouseCaptured && !_menu.IsOpen)
        {
            _anchorX = window.Left;
            _anchorY = window.Top;
            _hasAnchor = true;
            _dock = FloatingBehavior.DetectDock(window, work, scale);
        }
        var retract = _options.Peek && _dock != FloatingBehavior.DockSide.None && !Interacting && _hasAnchor;
        if (retract == _peeked) return;
        var target = retract
            ? FloatingBehavior.PeekPosition(window, work, _dock, 6, scale)
            : new System.Drawing.Point(_anchorX, _anchorY);
        _peeked = retract;
        SetWindowPos(handle, IntPtr.Zero, target.X, target.Y, 0, 0, 0x15 /* NOSIZE | NOZORDER | NOACTIVATE */);
    }

    private void Bubble_Down(object sender, MouseButtonEventArgs e)
    {
        _app.CloseRadial();
        _down = e.GetPosition(this);
        _dragged = false;
        CaptureMouse();
        RefreshInteractionState();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _down;
        if (_dragged || Math.Abs(delta.X) + Math.Abs(delta.Y) <= 6) return;
        _dragged = _dragging = true;
        ReleaseMouseCapture();
        RefreshInteractionState();
        try
        {
            DragMove();
            _peeked = false;
            _hasAnchor = false;
            ClampToScreen();
            try { _app.SaveSettings(_app.Settings with { FloatingLeft = Left, FloatingTop = Top }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _app.NotifySendResult("悬浮按钮位置未能保存；其他功能仍可使用。", true);
            }
        }
        finally { _dragging = false; RefreshInteractionState(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var wasCaptured = IsMouseCaptured;
        ReleaseMouseCapture();
        if (wasCaptured && !_dragged) _app.ToggleRadial();
        RefreshInteractionState();
        e.Handled = true;
    }

    // A retracted strip must still open on hover without waiting for a click.
    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        RefreshInteractionState();
    }

    private void Bubble_RightClick(object sender, MouseButtonEventArgs e)
    {
        _app.CloseRadial();
        _menu.PlacementTarget = Bubble;
        _menu.IsOpen = true;
        RefreshInteractionState();
        e.Handled = true;
    }

    private static string[]? DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        _dropHover = true;
        // Accept file payload delivery so rejected multi-file/unsupported drops can
        // explain the problem in Drop instead of silently disappearing at OLE level.
        e.Effects = DroppedPaths(e) is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        RefreshInteractionState();
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        _dropHover = false;
        RefreshInteractionState();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        _dropHover = false;
        var paths = DroppedPaths(e);
        _app.CloseRadial();
        if (FloatingBehavior.IsSingleImage(paths)) _app.OpenDroppedImage(paths![0]);
        else _app.NotifySendResult("请只拖入一张 PNG、JPG、GIF、WEBP 或 BMP 图片。", true);
        e.Handled = true;
        RefreshInteractionState();
    }

    private void ClampToScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = GetDpiForWindow(handle);
        var at = FloatingBehavior.Place(new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            work, _options.Snap, dpi == 0 ? 1 : dpi / 96d);
        SetWindowPos(handle, IntPtr.Zero, at.X, at.Y, 0, 0, 0x15 /* NOSIZE | NOZORDER | NOACTIVATE */);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
