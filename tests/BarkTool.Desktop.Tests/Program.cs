using System.Reflection;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BarkTool.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var input = Encoding.UTF8.GetBytes("test-only-secret-not-a-real-key");
            var encrypted = Dpapi.Protect(input);
            Check(!encrypted.SequenceEqual(input), "DPAPI ciphertext differs");
            Check(Dpapi.Unprotect(encrypted).SequenceEqual(input), "DPAPI round-trip");
            Console.WriteLine("PASS DPAPI current-user round-trip");

            var app = new App();
            app.InitializeComponent();
            var startupTestPath = @"Software\BarkTool.Tests\" + Guid.NewGuid().ToString("N");
            try
            {
                using var startupKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(startupTestPath);
                StartupRegistration.Write(startupKey, true, @"C:\Apps With Spaces\BarkTool.exe");
                Check((string?)startupKey.GetValue("BarkTool") == "\"C:\\Apps With Spaces\\BarkTool.exe\"", "startup command quoted");
                StartupRegistration.Write(startupKey, false, "");
                Check(startupKey.GetValue("BarkTool") is null, "startup entry removed");
                StartupRegistration.Write(startupKey, false, "");
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(startupTestPath, false); }
            Console.WriteLine("PASS startup registration (isolated registry key, actual startup unchanged)");
            LifecycleTests.Run();
            HotkeyTests.Run();
            Check(GameVisibility.Matches("Game.EXE", "game.exe\nother"), "game name matching ignores case and extension");
            Check(!GameVisibility.Matches("gameLauncher", "game.exe"), "game matches exact process not launcher");
            var enabledKeys = new HotkeySettings { Enabled = true };
            Check(!App.EffectiveHotkeys(enabledKeys, true).Enabled && enabledKeys.Enabled, "pause is runtime-only");
            Check(App.EffectiveHotkeys(enabledKeys, false).Enabled, "resume configured hotkeys");
            FloatingTests.Run();
            CaptureImageTests.Run();
            ClipboardSenderTests.Run().GetAwaiter().GetResult();
            var oldConfig = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
            Check(oldConfig.Hotkeys is null && oldConfig.Floating is null, "old config safe defaults");
            var settings = new SettingsWindow();
            Check(settings.FindName("ComposeEnabled") is CheckBox && settings.FindName("SaveRetry") is null, "hotkeys retained and retry settings removed");
            Check(settings.FindName("ApplyHotkeysButton") is System.Windows.Controls.Button, "independent hotkey apply");
            var hotkeyBox = (TextBox)settings.FindName("ComposeHotkey");
            Check(hotkeyBox.IsReadOnly, "hotkey boxes wait for a key press instead of text input");
            typeof(SettingsWindow).GetMethod("SetComboFromVirtualKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(settings, new object?[] { hotkeyBox, 0x42u, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt });
            Check(hotkeyBox.Text == "Ctrl+Alt+B", "captured combo from raw key press");
            typeof(SettingsWindow).GetMethod("SetComboFromVirtualKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(settings, new object?[] { hotkeyBox, 0x41u, System.Windows.Input.ModifierKeys.None });
            Check(hotkeyBox.Text == "Ctrl+Alt+B", "plain letter does not overwrite a combo");
            hotkeyBox.Text = "Ctrl+Shift+Q";
            var readHotkeys = (HotkeySettings)typeof(SettingsWindow).GetMethod("ReadHotkeys", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null)!;
            Check(readHotkeys.Compose == "Ctrl+Shift+Q", "edited combo read from UI");
            typeof(SettingsWindow).GetMethod("ResetHotkeys_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, new object[] { settings, new RoutedEventArgs() });
            Check(hotkeyBox.Text == "Ctrl+Alt+B", "hotkey reset without persisting user settings");
            var floating = new FloatWindow();
            Check(floating.Width == 40 && floating.Height == 40, "compact floating size");
            var floatingBubble = (Border)floating.FindName("Bubble");
            Check(floatingBubble.Background is LinearGradientBrush, "soft floating surface gradient");
            var arrow = (System.Windows.Shapes.Path)floatingBubble.Child;
            Check(arrow.Stroke is LinearGradientBrush && arrow.StrokeEndLineCap == PenLineCap.Round, "refined muted arrow");
            var floatingContent = (FrameworkElement)floating.Content;
            floatingContent.Measure(new Size(40, 40));
            floatingContent.Arrange(new Rect(0, 0, 40, 40));
            floatingContent.UpdateLayout();
            var iconRender = new RenderTargetBitmap(160, 160, 384, 384, PixelFormats.Pbgra32);
            iconRender.Render(floatingContent);
            Directory.CreateDirectory("artifacts");
            var iconEncoder = new PngBitmapEncoder();
            iconEncoder.Frames.Add(BitmapFrame.Create(iconRender));
            using (var file = File.Create("artifacts/floating-refined-preview.png")) iconEncoder.Save(file);
            Check(!floating.ShowActivated, "restore does not steal fullscreen focus");
            Check(App.ShouldShowFloating(false, false), "normal visibility");
            Check(!App.ShouldShowFloating(false, true), "fullscreen hides floating");
            Check(!App.ShouldShowFloating(true, false), "manual hide survives fullscreen exit");
            Check(!App.ShouldShowFloating(true, true), "manual and fullscreen hidden");
            var radial = new RadialMenuWindow();
            foreach (var name in new[] { "ComposeButton", "ClipboardButton", "ImageButton", "SettingsButton", "CollapseButton" })
                Check(radial.FindName(name) is System.Windows.Controls.Button, "radial action " + name);
            foreach (var name in new[] { "ComposeButton", "ClipboardButton", "ImageButton", "SettingsButton", "CollapseButton" })
            {
                var button = (System.Windows.Controls.Button)radial.FindName(name);
                Check(!string.IsNullOrWhiteSpace(button.ToolTip?.ToString()), "button description " + name);
                Check(!ToolTipService.GetIsEnabled(button), "popup tooltip disabled " + name);
                button.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                Check(((TextBlock)radial.FindName("HoverLabel")).Text == button.ToolTip?.ToString(), "hover label " + name);
                button.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                Check(((TextBlock)radial.FindName("HoverLabel")).Text == "选择操作", "hover reset " + name);
            }
            Check(((System.Windows.Controls.Button)radial.FindName("ClipboardButton")).ToolTip.ToString() == "直接发送剪贴板", "direct send description");
            Check(radial.Width == 260 && radial.Topmost && !radial.ShowInTaskbar, "radial window flags");
            var radialContent = (FrameworkElement)radial.Content;
            radialContent.Measure(new Size(260, 260));
            radialContent.Arrange(new Rect(0, 0, 260, 260));
            radialContent.UpdateLayout();
            var rendered = new RenderTargetBitmap(260, 260, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(radialContent);
            var previewEncoder = new PngBitmapEncoder();
            previewEncoder.Frames.Add(BitmapFrame.Create(rendered));
            Directory.CreateDirectory("artifacts");
            using (var previewFile = File.Create("artifacts/radial-preview.png")) previewEncoder.Save(previewFile);
            Console.WriteLine("PASS compact radial layout and fullscreen visibility policy");
            var quick = new SendWindow();
            quick.ApplySimpleMode(true);
            Check(quick.Title.Contains("快捷发送") && quick.Width == 430 && quick.Height == 520, "compact shortcut send window");
            Check(((TextBox)quick.FindName("BodyBox")).MinHeight == 120 && ((TextBox)quick.FindName("BodyBox")).VerticalAlignment == VerticalAlignment.Top, "quick body has fixed top-aligned space");
            Check(quick.FindName("SettingsButton") is Button && ((Button)quick.FindName("SettingsButton")).Visibility == Visibility.Collapsed, "simple window hides settings");
            Check(((TextBlock)quick.FindName("ImageLabel")).Text.Contains("Ctrl+V"), "quick window explains image paste");
            var send = new SendWindow();
            Check(send.FindName("TitleBox") is null, "no title input");
            Check(send.FindName("BodyBox") is TextBox, "body input remains");
            Check(settings.Title.Contains("设置"), "settings XAML");
            Check(settings.Icon is not null && send.Icon is not null, "application window icons");
            using (var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/BarkTool;component/Resources/bark.ico")).Stream)
            using (var trayIcon = new System.Drawing.Icon(iconStream, 32, 32))
                Check(trayIcon.Width == 32 && trayIcon.Height == 32, "tray icon resource");
            Check(((Border)floating.FindName("Bubble")).Child is System.Windows.Shapes.Path, "original arrow icon restored");
            Check(floating.Topmost && !floating.ShowInTaskbar, "floating window flags");
            var status = (TextBlock)send.FindName("Status");
            var sendContent = (FrameworkElement)send.Content;
            sendContent.Measure(new Size(438, 510));
            sendContent.Arrange(new Rect(0, 0, 438, 510));
            sendContent.IsEnabled = false;
            sendContent.UpdateLayout();
            var bodyInput = (TextBox)send.FindName("BodyBox");
            bodyInput.ApplyTemplate();
            var inputSurface = (Border)bodyInput.Template.FindName("InputSurface", bodyInput);
            Check(((SolidColorBrush)inputSurface.Background).Color == Color.FromRgb(23, 34, 53), "disabled input stays dark");
            var sendAction = (System.Windows.Controls.Button)send.FindName("SendButton");
            sendAction.ApplyTemplate();
            var buttonSurface = (Border)sendAction.Template.FindName("ButtonSurface", sendAction);
            Check(((SolidColorBrush)buttonSurface.Background).Color == Color.FromRgb(34, 105, 95), "disabled send stays green");
            Check(buttonSurface.Opacity < 1, "disabled state uses opacity instead of white fill");
            var disabledPreview = new RenderTargetBitmap(438, 510, 96, 96, PixelFormats.Pbgra32);
            disabledPreview.Render(sendContent);
            var disabledEncoder = new PngBitmapEncoder();
            disabledEncoder.Frames.Add(BitmapFrame.Create(disabledPreview));
            using (var output = File.Create("artifacts/send-disabled-preview.png")) disabledEncoder.Save(output);
            sendContent.IsEnabled = true;
            Console.WriteLine("PASS disabled send controls retain dark palette");
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            using var image = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(image);
            typeof(SendWindow).GetMethod("SetImage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(send, [image.ToArray(), "test.png"]);
            Check(((Image)send.FindName("Preview")).Source is not null, "image preview loaded");
            Check(status.Text.Contains("预览"), "image preview does not upload");
            var imageConfirmation = new SendWindow();
            imageConfirmation.EnableImageConfirmation();
            Check(imageConfirmation.Title.Contains("确认发送图片"), "dedicated confirmation mode");
            Check(((Border)imageConfirmation.FindName("PreviewFrame")).Height == 180, "large image preview");
            Check(((System.Windows.Controls.Button)imageConfirmation.FindName("CancelButton")).Visibility == Visibility.Visible, "explicit cancel");
            Check(!imageConfirmation.IsVisible && !imageConfirmation.IsSending, "confirmation does not auto upload");
            Check(bodyInput.Text.Length == 0, "separate confirmation leaves original draft unchanged");
            Console.WriteLine("PASS all WPF windows and image confirmation mode");
            bodyInput.Text = "待发送内容";
            send.Show();
            send.HideForSending();
            Check(!send.IsVisible, "send panel hides immediately for background sending");
            Check(bodyInput.Text == "待发送内容", "hiding retains draft");
            Console.WriteLine("PASS send panel hides without closing");
            app.Http.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
