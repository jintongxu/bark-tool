using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using BarkTool.Core;
using Microsoft.Win32;

namespace BarkTool.Desktop;

public partial class SendWindow : Window
{
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private readonly App _app = (App)Application.Current;
    private byte[]? _image;
    private string _fileName = "clipboard.png";
    private readonly List<(byte[] Bytes, string Name)> _queuedImages = new();
    private bool _sending;

    public SendWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (!_app.IsExiting) { e.Cancel = true; Hide(); } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;
                Send_Click(this, new RoutedEventArgs());
                return;
            }
            if (e.Key != System.Windows.Input.Key.V || !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) return;
            if (e.KeyboardDevice.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            if (!Clipboard.ContainsImage()) return; // Let text paste behave normally.
            e.Handled = true;
            PasteFromClipboard();
        };
    }

    public void ReadClipboard()
    {
        if (_sending) return;
        try
        {
            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap is null) throw new InvalidOperationException("未能读取剪贴板图片。");
                using var bytes = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(bytes);
                SetImage(bytes.ToArray(), "clipboard.png");
                Status.Text = "剪贴板截图已加入预览，点击发送才会上传。";
            }
            else if (Clipboard.ContainsText())
            {
                BodyBox.Text = Clipboard.GetText();
                Status.Text = "已读取剪贴板文字，点击发送才会推送。";
            }
            else Status.Text = "剪贴板中没有文字或图片。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Status.Text = "剪贴板暂时被其他程序占用，请稍后重试。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FileFormatException)
        {
            Status.Text = ex.Message;
        }
    }

    private async void Clipboard_Click(object sender, RoutedEventArgs e) => await _app.SendClipboardAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();
    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        _image = null;
        _queuedImages.Clear();
        Preview.Source = null;
        ImageLabel.Text = "未选择图片；支持拖入、选文件或读取截图";
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e) => ChooseImage();

    internal void ChooseImage()
    {
        if (_sending) return;
        var dialog = new OpenFileDialog { Title = "选择要发送的图片（可多选）", Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            if (dialog.FileNames.Length == 1) LoadImageFile(dialog.FileName);
            else LoadMultipleImages(dialog.FileNames);
        }
    }

    private void LoadMultipleImages(string[] paths)
    {
        try
        {
            var images = new List<(byte[] Bytes, string Name)>();
            foreach (var path in paths)
            {
                if (!FloatingBehavior.IsSingleImage([path])) throw new ArgumentException("请选择图片文件。");
                var optimized = ImageOptimizer.Process(File.ReadAllBytes(path), Path.GetFileName(path), _app.Settings.ImageOptimization ?? new ImageOptimizationOptions());
                if (optimized.Bytes.Length > MaxImageBytes) throw new ArgumentException($"图片不能超过 10 MiB：{optimized.FileName}");
                images.Add((optimized.Bytes, optimized.FileName));
            }
            _queuedImages.Clear(); _queuedImages.AddRange(images);
            SetImage(images[0].Bytes, images[0].Name);
            ImageLabel.Text = $"已选择 {images.Count} 张图片，发送时将逐张推送";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        { Status.Text = ex.Message; }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_sending) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) LoadImageFile(files[0]);
        else if (e.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap) SetBitmap(bitmap);
        else Status.Text = "一次只能拖入一张图片。";
    }

    // Ctrl+V pastes a clipboard image straight into the preview; text still goes to the body.
    internal void PasteFromClipboard()
    {
        if (_sending) return;
        try
        {
            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap is null) { Status.Text = "未能读取剪贴板图片。"; return; }
                SetBitmap(bitmap);
                Status.Text = "剪贴板图片已加入预览，点击发送才会上传。";
                return;
            }
            if (Clipboard.ContainsText()) BodyBox.Paste();
            else Status.Text = "剪贴板中没有图片或文字。";
        }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "剪贴板暂时被其他程序占用，请稍后重试。"; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FileFormatException) { Status.Text = ex.Message; }
    }

    private void SetBitmap(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        SetImage(stream.ToArray(), "clipboard.png");
    }

    internal bool LoadImageFile(string path)
    {
        try
        {
            if (!new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                throw new ArgumentException("请选择 PNG、JPEG、GIF、WebP 或 BMP 图片。");
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new ArgumentException("原始图片不能超过 64 MiB。");
            SetImage(File.ReadAllBytes(path), Path.GetFileName(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Status.Text = ex is ArgumentException ? ex.Message : "无法读取所选图片。";
            return false;
        }
    }

    internal bool IsSending => _sending;

    internal void ApplySimpleMode(bool simple)
    {
        if (!simple) return;
        var state = QuickWindowStore.Load();
        Title = "Bark · 快捷发送";
        Width = 430;
        Height = state.Height;
        Width = state.Width;
        if (state.Left is double left && state.Top is double top) { Left = left; Top = top; }
        MinWidth = 400;
        MinHeight = 480;
        SettingsButton.Visibility = Visibility.Collapsed;
        Heading.Text = "快捷发送";
        Subtitle.Text = "输入内容后按 Ctrl+Enter 发送";
        BodyBox.MinHeight = 120;
        BodyBox.Height = 120;
        BodyBox.VerticalAlignment = VerticalAlignment.Top;
        LocationChanged += (_, _) => SaveQuickState();
        SizeChanged += (_, _) => SaveQuickState();
        PreviewFrame.Height = 52;
        PreviewFrame.Width = 52;
    }
    internal bool HasPendingImage => _image is not null;
    internal string StatusMessage => Status.Text;

    internal void EnableImageConfirmation()
    {
        Title = "Bark · 确认发送图片";
        Heading.Text = "确认发送图片";
        Subtitle.Text = "确认前仅本地预览，不会上传";
        BodyLabel.Text = "附言（可选）";
        BodyBox.MinHeight = 60;
        BodyBox.Height = 70;
        ContentRow.Height = GridLength.Auto;
        PreviewFrame.Width = double.NaN;
        PreviewFrame.Height = 180;
        PreviewFrame.Margin = new Thickness(0, 0, 0, 12);
        System.Windows.Controls.DockPanel.SetDock(PreviewFrame, System.Windows.Controls.Dock.Top);
        SendButton.Content = "确认发送 ↗";
        CancelButton.Visibility = Visibility.Visible;
        Status.Text = "确认后才会上传图片并加密推送。";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ClearImage_Click(sender, e);
        BodyBox.Clear();
        Hide();
    }

    internal void SetImage(byte[] bytes, string fileName)
    {
        var optimized = ImageOptimizer.Process(bytes, fileName, _app.Settings.ImageOptimization ?? new ImageOptimizationOptions());
        bytes = optimized.Bytes;
        fileName = optimized.FileName;
        if (bytes.Length > MaxImageBytes) throw new ArgumentException("图片不能超过 10 MiB。");
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(bytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();
        Preview.Source = bitmap;
        _image = bytes;
        _queuedImages.Clear();
        _queuedImages.Add((bytes, fileName));
        _fileName = fileName;
        ImageLabel.Text = $"{fileName} · {optimized.Width}×{optimized.Height}\n{optimized.OriginalBytes / 1024d:0.#} KB → {bytes.Length / 1024d:0.#} KB";
        Status.Text = "图片已加入预览，点击发送才会上传。";
    }

    internal void HideForSending()
    {
        _sending = true;
        Hide();
    }

    private void SaveQuickState() => QuickWindowStore.Save(new QuickWindowState(Left, Top, Width, Height));

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var settings = _app.Settings;
        if (string.IsNullOrWhiteSpace(settings.BarkUrl) || string.IsNullOrWhiteSpace(settings.DeviceKey))
        {
            Status.Text = "请先在设置中填写 Bark 地址和设备 Key。";
            _app.OpenSettings();
            return;
        }
        if (_image is null && string.IsNullOrWhiteSpace(BodyBox.Text))
        {
            Status.Text = "请先输入文字或添加图片。";
            return;
        }
        try
        {
            BarkEncryption.ValidateKey(settings.EncryptionKey);
            if (_image is not null && (string.IsNullOrWhiteSpace(settings.ImageUrl) || string.IsNullOrWhiteSpace(settings.ImageToken)))
                throw new InvalidOperationException("发送图片需要先配置 EasyImage 地址和上传 Token。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status.Text = ex.Message;
            return;
        }
        HideForSending();
        ((UIElement)Content).IsEnabled = false;
        SendingProgress.Visibility = Visibility.Visible;
        SendButton.Content = "发送中…";
        Status.Text = "正在加密并发送，请稍候…";
        try
        {
            BarkEncryption.ValidateKey(settings.EncryptionKey);
            var body = BodyBox.Text.Trim();
            var images = _queuedImages.Count > 0 ? _queuedImages.ToList() : new List<(byte[] Bytes, string Name)>();
            if (images.Count == 0 && _image is not null) images.Add((_image, _fileName));
            for (var index = 0; index < images.Count; index++)
            {
                Status.Text = images.Count > 1 ? $"正在发送第 {index + 1}/{images.Count} 张…" : "正在发送…";
                await _app.Coordinator.SendAsync(new SendDraft(body, images[index].Bytes, images[index].Name, ImagePrepared: true), stage => Dispatcher.Invoke(() => Status.Text = stage));
            }
            if (images.Count == 0)
                await _app.Coordinator.SendAsync(new SendDraft(body), stage => Dispatcher.Invoke(() => Status.Text = stage));
            Status.Text = images.Count > 1 ? $"已发送 {images.Count} 张图片。" : "已发送到 Bark。";
            _app.NotifySendResult(Status.Text, false);
            BodyBox.Clear();
            ClearImage_Click(sender, e);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        {
            Status.Text = ex is HttpRequestException or TaskCanceledException ? "网络请求失败或超时，请检查服务器地址和网络。" : ex.Message;
            _app.NotifySendResult(Status.Text, true);
        }
        finally
        {
            _sending = false;
            ((UIElement)Content).IsEnabled = true;
            SendingProgress.Visibility = Visibility.Collapsed;
            SendButton.Content = CancelButton.Visibility == Visibility.Visible ? "确认发送 ↗" : "发送 ↗";
        }
    }
}
