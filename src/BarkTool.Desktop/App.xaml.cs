using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BarkTool.Desktop;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private FloatWindow? _floating;
    private SendWindow? _send;
    private SendWindow? _imageConfirmation;
    private bool _choosingImage;
    private bool _capturing;
    private SettingsWindow? _settings;
    private SingleInstance? _instance;
    private RadialMenuWindow? _radial;
    private bool _manuallyHidden;
    private ClipboardSender? _clipboardSender;
    private HotkeyManager? _hotkeys;
    private bool _hotkeysPaused;
    private Forms.ToolStripMenuItem? _pauseHotkeys;
    private SendCoordinator? _coordinator;
    internal SendCoordinator Coordinator => _coordinator ??= new SendCoordinator(Http, () => Settings);
    internal FloatingOptions FloatingOptions => Settings.Floating ?? new FloatingOptions();
    internal bool IsRadialOpen => _radial?.IsVisible == true;
    internal AppSettings Settings { get; private set; } = new();
    internal bool IsExiting { get; private set; }
    internal HttpClient Http { get; } = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance();
        if (!_instance.IsPrimary)
        {
            _instance.RequestActivation();
            IsExiting = true;
            Shutdown();
            return;
        }
        _instance.StartListening(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsExiting) OpenSend();
        })));
        try { Settings = SettingsStore.Load(); }
        catch (InvalidOperationException)
        {
            MessageBox.Show("无法读取本地设置。为避免覆盖原配置，程序将退出。请检查 %LOCALAPPDATA%\\BarkTool。", "Bark", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        using (var iconStream = GetResourceStream(new Uri("pack://application:,,,/BarkTool;component/Resources/bark.ico")).Stream)
            _trayIcon = new Icon(iconStream, 32, 32);
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "Bark 快速发送",
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("框选截图", null, async (_, _) => await CaptureAsync());
        _tray.ContextMenuStrip.Items.Add("打开发送面板", null, (_, _) => OpenSend());
        _tray.ContextMenuStrip.Items.Add("直接发送剪贴板", null, async (_, _) => await SendClipboardAsync());
        _pauseHotkeys = new Forms.ToolStripMenuItem("暂停全局快捷键") { CheckOnClick = false };
        _pauseHotkeys.Click += (_, _) => ToggleHotkeyPause();
        _tray.ContextMenuStrip.Items.Add(_pauseHotkeys);
        _tray.ContextMenuStrip.Items.Add("设置", null, (_, _) => OpenSettings());
        _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => { IsExiting = true; Shutdown(); });
        _tray.DoubleClick += (_, _) => OpenSend();
        // The floating button is no longer part of the default workflow. The hidden
        // native window remains only as a stable message target for global hotkeys.
        _floating = new FloatWindow { ShowInTaskbar = false, Visibility = Visibility.Hidden };
        _floating.Show();
        _floating.Hide();
        var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(_floating).Handle);
        _hotkeys = new HotkeyManager(source!, HandleHotkey);
        try { _hotkeys.Apply(Settings.Hotkeys ?? new HotkeySettings()); }
        catch (HotkeyConfigurationException ex) { NotifySendResult(ex.Message, true); }
        if (string.IsNullOrEmpty(Settings.BarkUrl) || string.IsNullOrEmpty(Settings.DeviceKey) || string.IsNullOrEmpty(Settings.EncryptionKey)) OpenSettings();
    }

    private void ToggleHotkeyPause()
    {
        var paused = !_hotkeysPaused;
        try
        {
            _hotkeys?.Apply(EffectiveHotkeys(Settings.Hotkeys ?? new HotkeySettings(), paused));
            _hotkeysPaused = paused;
            if (_pauseHotkeys is not null)
            {
                _pauseHotkeys.Checked = paused;
                _pauseHotkeys.Text = paused ? "恢复全局快捷键" : "暂停全局快捷键";
            }
        }
        catch (HotkeyConfigurationException ex) { NotifySendResult(ex.Message, true); }
    }

    internal static HotkeySettings EffectiveHotkeys(HotkeySettings settings, bool paused) => paused ? settings with { Enabled = false } : settings;

    private async void HandleHotkey(HotkeyAction action)
    {
        if (_hotkeysPaused || IsExiting) return;
        switch (action)
        {
            case HotkeyAction.Compose: OpenQuickSend(); break;
            case HotkeyAction.Clipboard: await SendClipboardAsync(); break;
            case HotkeyAction.Image: OpenImage(); break;
            case HotkeyAction.Capture: await CaptureAsync(); break;
        }
    }

    internal async Task CaptureAsync()
    {
        if (_capturing) return;
        if (_imageConfirmation?.IsSending == true) { NotifySendResult("图片正在发送，请稍候。", true); return; }
        if (_imageConfirmation?.HasPendingImage == true && MessageBox.Show("替换当前未发送的图片预览？", "Bark", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _capturing = true;
        CloseRadial();
        var restoreSend = _send?.IsVisible == true;
        var restoreImage = _imageConfirmation?.IsVisible == true;
        _send?.Hide(); _imageConfirmation?.Hide(); _floating?.Hide();
        var completed = false;
        try
        {
            await Task.Delay(150); // Allow hidden windows to leave the desktop compositor before capture.
            var bytes = await ScreenCapture.CaptureAsync();
            if (bytes is null) return;
            var confirmation = new SendWindow();
            confirmation.SetImage(bytes, "screenshot.png");
            confirmation.EnableImageConfirmation();
            _imageConfirmation = confirmation;
            confirmation.Show(); confirmation.Activate();
            completed = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { NotifySendResult(ex.Message, true); }
        finally
        {
            _capturing = false;
            if (restoreSend) _send?.Show();
            if (!completed && restoreImage) _imageConfirmation?.Show();
            UpdateFloatingVisibility();
        }
    }

    internal void ExitApplication() { IsExiting = true; Shutdown(); }

    internal void OpenDroppedImage(string path)
    {
        if (_imageConfirmation?.IsSending == true) { NotifySendResult("图片正在发送，请稍候。", true); return; }
        if (_imageConfirmation?.HasPendingImage == true
            && MessageBox.Show("替换尚未发送的图片预览？", "Bark", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var confirmation = new SendWindow();
        if (!confirmation.LoadImageFile(path)) { NotifySendResult(confirmation.StatusMessage, true); return; }
        _imageConfirmation?.Hide();
        confirmation.EnableImageConfirmation();
        _imageConfirmation = confirmation;
        confirmation.Show();
        confirmation.Activate();
    }

    internal void OpenQuickSend()
    {
        _send ??= new SendWindow();
        _send.ApplySimpleMode(true);
        if (!_send.IsVisible) _send.Show();
        if (_send.WindowState == WindowState.Minimized) _send.WindowState = WindowState.Normal;
        _send.Activate();
        _send.BodyBox.Focus();
    }

    internal void OpenSend()
    {
        _send ??= new SendWindow();
        if (!_send.IsVisible) _send.Show();
        if (_send.WindowState == WindowState.Minimized) _send.WindowState = WindowState.Normal;
        _send.Activate();
    }

    internal async Task SendClipboardAsync()
    {
        _clipboardSender ??= new ClipboardSender(Http, coordinator: Coordinator);
        try
        {
            await _clipboardSender.SendAsync(Settings);
            NotifySendResult("剪贴板已发送到 Bark。", false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            NotifySendResult(ex is HttpRequestException or TaskCanceledException
                ? "网络请求失败或超时，请检查服务器地址和网络。" : ex.Message, true);
        }
    }

    internal void NotifySendResult(string message, bool failed)
    {
        if (IsExiting || _tray is null) return;
        _tray.ShowBalloonTip(4000, failed ? "Bark · 发送失败" : "Bark · 发送成功", message,
            failed ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    internal void OpenImage()
    {
        if (_choosingImage) return;
        if (_imageConfirmation is not null)
        {
            if (_imageConfirmation.IsSending)
            {
                NotifySendResult("图片正在发送，请稍候。", true);
                return;
            }
            if (_imageConfirmation.IsVisible || _imageConfirmation.HasPendingImage)
            {
                _imageConfirmation.Show();
                _imageConfirmation.WindowState = WindowState.Normal;
                _imageConfirmation.Activate();
                return;
            }
        }
        _choosingImage = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择图片，下一步预览确认", Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp", Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;
            var confirmation = new SendWindow();
            if (!confirmation.LoadImageFile(dialog.FileName))
            {
                NotifySendResult(confirmation.StatusMessage, true);
                return;
            }
            confirmation.EnableImageConfirmation();
            _imageConfirmation = confirmation;
            confirmation.Show();
            confirmation.Activate();
        }
        finally { _choosingImage = false; }
    }

    internal void ToggleRadial()
    {
        if (_floating is null) return;
        if (_radial is null)
        {
            _radial = new RadialMenuWindow();
            _radial.IsVisibleChanged += (_, _) => _floating?.RefreshInteractionState();
        }
        if (_radial.IsVisible) _radial.Hide();
        else _radial.OpenAt(_floating);
    }

    internal void CloseRadial() => _radial?.Hide();

    internal void SetFloatingHidden(bool hidden)
    {
        _manuallyHidden = hidden;
        UpdateFloatingVisibility();
    }

    internal static bool ShouldShowFloating(bool manuallyHidden, bool fullScreen) => !manuallyHidden && !fullScreen;

    private void UpdateFloatingVisibility()
    {
        if (_floating is null || IsExiting || _capturing) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(_floating).Handle;
        var visible = ShouldShowFloating(_manuallyHidden, FullScreenDetector.IsFullScreenOnMonitor(handle) || GameVisibility.IsForegroundGame(Settings.GameProcesses));
        if (visible)
        {
            if (!_floating.IsVisible) _floating.Show();
        }
        else
        {
            CloseRadial();
            if (_floating.IsVisible) _floating.Hide();
        }
    }

    internal void OpenSettings()
    {
        _settings ??= new SettingsWindow();
        if (!_settings.IsVisible) _settings.Show();
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    internal void SaveHotkeys(HotkeySettings hotkeys)
    {
        if (!HotkeyManager.TryValidate(hotkeys, out var error)) throw new HotkeyConfigurationException(error);
        SaveSettings(Settings with { Hotkeys = hotkeys });
    }

    internal void SaveSettings(AppSettings settings)
    {
        var previous = EffectiveHotkeys(Settings.Hotkeys ?? new HotkeySettings(), _hotkeysPaused);
        _hotkeys?.Apply(EffectiveHotkeys(settings.Hotkeys ?? new HotkeySettings(), _hotkeysPaused));
        try { SettingsStore.Save(settings); }
        catch { _hotkeys?.Apply(previous); throw; }
        Settings = settings;
        _floating?.ApplyOptions(FloatingOptions);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _hotkeys?.Dispose();
        _instance?.Dispose();
        _tray?.Dispose();
        _trayIcon?.Dispose();
        Http.Dispose();
        base.OnExit(e);
    }
}
