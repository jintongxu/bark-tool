using System.Windows;
using BarkTool.Core;

namespace BarkTool.Desktop;

public partial class SettingsWindow : Window
{
    private readonly App _app = (App)Application.Current;

    public SettingsWindow()
    {
        InitializeComponent();
        var settings = _app.Settings;
        BarkUrl.Text = settings.BarkUrl;
        DeviceKey.Password = settings.DeviceKey;
        ImageUrl.Text = settings.ImageUrl;
        ImageToken.Password = settings.ImageToken;
        Group.Text = settings.Group;
        EncryptionKey.Password = settings.EncryptionKey;
        NotificationMode.SelectedValue = settings.NotificationLevel;
        AutoCopyEnabled.IsChecked = settings.AutoCopy;
        GameNames.Text = settings.GameProcesses;
        var optimization = settings.ImageOptimization ?? new ImageOptimizationOptions();
        OptimizeImages.IsChecked = optimization.Enabled;
        MaxImageDimension.Value = optimization.MaxDimension;
        ImageQuality.Value = optimization.JpegQuality;
        var hotkeys = settings.Hotkeys ?? new HotkeySettings();
        EnableHotkeys.IsChecked = hotkeys.Enabled;
        ComposeEnabled.IsChecked = hotkeys.ComposeEnabled;
        ClipboardEnabled.IsChecked = hotkeys.ClipboardEnabled;
        ImageEnabled.IsChecked = hotkeys.ImageEnabled;
        ComposeHotkey.Text = hotkeys.Compose;
        ClipboardHotkey.Text = hotkeys.Clipboard;
        ImageHotkey.Text = hotkeys.Image;
        CaptureHotkeyBox.Text = hotkeys.Capture;
        CaptureEnabled.IsChecked = hotkeys.CaptureEnabled;
        try { StartWithWindows.IsChecked = StartupRegistration.IsEnabled(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            StartWithWindows.IsEnabled = false;
            Status.Text = "无法读取 Windows 启动项，请检查当前用户权限。";
        }
        foreach (var box in new[] { ComposeHotkey, ClipboardHotkey, ImageHotkey, CaptureHotkeyBox })
        {
            box.IsReadOnly = true;
            box.Cursor = System.Windows.Input.Cursors.Arrow;
            box.ToolTip = "点击后直接按下新的组合键";
        }
        Closing += (_, e) => { if (!_app.IsExiting) { e.Cancel = true; Hide(); } };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bark = BarkUrl.Text.Trim();
            var image = ImageUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(bark) || string.IsNullOrWhiteSpace(DeviceKey.Password))
                throw new ArgumentException("请填写 Bark 服务器地址和设备 Key。");
            EndpointRules.BarkPush(bark);
            BarkEncryption.ValidateKey(EncryptionKey.Password);
            if (image.Length > 0) EndpointRules.EasyImageUpload(image);
            if ((image.Length > 0) != (ImageToken.Password.Length > 0))
                throw new ArgumentException("请同时填写图床地址和上传 Token，或全部留空。");
            var hotkeys = ReadHotkeys();
            if (!HotkeyManager.TryValidate(hotkeys, out var hotkeyError)) throw new ArgumentException(hotkeyError);
            _app.SaveSettings(_app.Settings with
            {
                BarkUrl = bark, DeviceKey = DeviceKey.Password.Trim(),
                ImageUrl = image, ImageToken = ImageToken.Password.Trim(), Group = Group.Text.Trim(),
                EncryptionKey = EncryptionKey.Password,
                ImageOptimization = new ImageOptimizationOptions(OptimizeImages.IsChecked == true, (int)MaxImageDimension.Value, (int)ImageQuality.Value),
                GameProcesses = string.Join(Environment.NewLine, GameVisibility.Parse(GameNames.Text)),
                NotificationLevel = NotificationMode.SelectedValue as string ?? "active", AutoCopy = AutoCopyEnabled.IsChecked == true,
                Hotkeys = hotkeys
            });
            Status.Text = "设置已保存到当前 Windows 用户。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Status.Text = ex is IOException or UnauthorizedAccessException ? "无法保存设置，请检查本机用户数据目录权限。" : ex.Message;
        }
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择游戏实际运行的 EXE（不是启动器）", Filter = "程序|*.exe" };
        if (dialog.ShowDialog(this) == true)
            GameNames.Text = string.Join(Environment.NewLine, GameVisibility.Parse(GameNames.Text + Environment.NewLine + Path.GetFileName(dialog.FileName)));
    }

    private HotkeySettings ReadHotkeys() => new()
    {
        Enabled = EnableHotkeys.IsChecked == true,
        Compose = ComposeHotkey.Text, Clipboard = ClipboardHotkey.Text, Image = ImageHotkey.Text,
        ComposeEnabled = ComposeEnabled.IsChecked == true, ClipboardEnabled = ClipboardEnabled.IsChecked == true,
        ImageEnabled = ImageEnabled.IsChecked == true,
        Capture = CaptureHotkeyBox.Text, CaptureEnabled = CaptureEnabled.IsChecked == true
    };

    private void ApplyHotkeys_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _app.SaveHotkeys(ReadHotkeys());
            HotkeyStatus.Text = EnableHotkeys.IsChecked == true ? "快捷键已应用，立即生效。" : "全局快捷键已关闭。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            HotkeyStatus.Text = ex is IOException or UnauthorizedAccessException ? "保存快捷键失败，请检查本机配置目录权限。" : ex.Message;
        }
    }

    private void SetComboFromVirtualKey(System.Windows.Controls.TextBox box, uint virtualKey, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!HotkeyCombination.TryFromVirtualKey(modifiers, virtualKey, out var combination))
        {
            HotkeyStatus.Text = "请使用 Ctrl/Alt/Shift/Win 加字母、数字或 F1–F24。";
            return;
        }
        box.Text = combination.ToString();
        HotkeyStatus.Text = "组合键已修改，点击“应用快捷键”生效。";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new HotkeySettings();
        ComposeHotkey.Text = defaults.Compose;
        ClipboardHotkey.Text = defaults.Clipboard;
        ImageHotkey.Text = defaults.Image;
        CaptureHotkeyBox.Text = defaults.Capture;
        HotkeyStatus.Text = "已恢复默认组合，点击“应用快捷键”生效。";
    }

    // Read-only boxes: a click must wait for the user's next key press instead of typing.
    private void CaptureHotkey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || e.Key == System.Windows.Input.Key.Tab) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.DeadCharProcessed or System.Windows.Input.Key.ImeProcessed)
        {
            e.Handled = true;
            HotkeyStatus.Text = "请再按一次需要设置的组合键。";
            return;
        }
        e.Handled = true;
        SetComboFromVirtualKey(box, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key), System.Windows.Input.Keyboard.Modifiers);
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        var enabled = StartWithWindows.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            Status.Text = enabled ? "已启用开机自启，登录 Windows 后自动运行。" : "已关闭开机自启。";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            StartWithWindows.IsChecked = !enabled;
            Status.Text = "开机自启设置失败，请检查当前用户权限并从发布版 EXE 运行。";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
