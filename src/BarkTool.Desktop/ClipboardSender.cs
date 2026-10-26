using System.Windows;
using System.Windows.Media.Imaging;
using BarkTool.Core;

namespace BarkTool.Desktop;

internal sealed record ClipboardContent(string? Text = null, byte[]? Image = null);

internal sealed class ClipboardSender(HttpClient http, Func<ClipboardContent>? read = null, SendCoordinator? coordinator = null)
{
    internal bool IsSending { get; private set; }

    internal async Task SendAsync(AppSettings settings)
    {
        if (IsSending) throw new InvalidOperationException("剪贴板正在发送，请稍候。");
        IsSending = true;
        try
        {
            if (string.IsNullOrWhiteSpace(settings.BarkUrl) || string.IsNullOrWhiteSpace(settings.DeviceKey))
                throw new InvalidOperationException("请先在设置中填写 Bark 地址和设备 Key。");
            BarkEncryption.ValidateKey(settings.EncryptionKey);
            var content = (read ?? ReadClipboard)();
            string body;
            string? imageUrl = null;
            if (coordinator is not null)
            {
                await coordinator.SendAsync(new SendDraft(content.Text ?? "", content.Image));
                return;
            }
            if (content.Image is not null)
            {
                if (content.Image.Length > 10 * 1024 * 1024)
                    throw new ArgumentException("图片不能超过 10 MiB。");
                if (string.IsNullOrWhiteSpace(settings.ImageUrl) || string.IsNullOrWhiteSpace(settings.ImageToken))
                    throw new InvalidOperationException("发送图片需要先配置 EasyImage 地址和上传 Token。");
                using var stream = new MemoryStream(content.Image, writable: false);
                imageUrl = await new EasyImageClient(http).UploadAsync(settings.ImageUrl, settings.ImageToken, stream, "clipboard.png");
                body = "点击查看图片";
            }
            else
            {
                body = content.Text ?? "";
                if (string.IsNullOrWhiteSpace(body))
                    throw new InvalidOperationException("剪贴板中没有可发送的文字或图片。");
            }
            await new BarkClient(http).SendAsync(settings.BarkUrl, settings.DeviceKey, settings.Group, new PushMessage(body, imageUrl, settings.NotificationLevel, settings.AutoCopy), encryptionKey: settings.EncryptionKey);
        }
        finally { IsSending = false; }
    }

    private static ClipboardContent ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage() ?? throw new InvalidOperationException("未能读取剪贴板图片。");
                using var stream = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
                return new ClipboardContent(Image: stream.ToArray());
            }
            return Clipboard.ContainsText() ? new ClipboardContent(Text: Clipboard.GetText()) : new ClipboardContent();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            throw new InvalidOperationException("剪贴板暂时被其他程序占用，请稍后重试。");
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
        {
            throw new InvalidOperationException("无法读取剪贴板图片，请重新截图后重试。");
        }
    }
}
