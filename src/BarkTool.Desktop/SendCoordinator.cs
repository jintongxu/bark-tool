using BarkTool.Core;

namespace BarkTool.Desktop;

internal sealed record SendDraft(string Body, byte[]? Image = null, string FileName = "clipboard.png", bool ImagePrepared = false);

// One attempt only: no history, persistent payload, or retry cache.
internal sealed class SendCoordinator(HttpClient http, Func<AppSettings> currentSettings)
{
    internal async Task SendAsync(SendDraft draft, Action<string>? stage = null)
    {
        stage?.Invoke("正在准备发送…");
        var settings = currentSettings();
        BarkEncryption.ValidateKey(settings.EncryptionKey);
        _ = EndpointRules.BarkPush(settings.BarkUrl);
        if (string.IsNullOrWhiteSpace(settings.DeviceKey)) throw new ArgumentException("请先配置设备 Key。");
        if (draft.Image is null && string.IsNullOrWhiteSpace(draft.Body)) throw new ArgumentException("请输入内容或选择图片。");
        string? imageUrl = null;
        if (draft.Image is not null)
        {
            stage?.Invoke("正在上传图片…");
            if (!draft.ImagePrepared && settings.ImageOptimization?.Enabled == true)
            {
                var optimized = ImageOptimizer.Process(draft.Image, draft.FileName, settings.ImageOptimization);
                draft = draft with { Image = optimized.Bytes, FileName = optimized.FileName };
            }
            if (draft.Image.Length > 10 * 1024 * 1024) throw new ArgumentException("图片不能超过 10 MiB。");
            if (string.IsNullOrWhiteSpace(settings.ImageToken)) throw new ArgumentException("请先配置图床上传 Token。");
            using var stream = new MemoryStream(draft.Image, writable: false);
            imageUrl = await new EasyImageClient(http).UploadAsync(settings.ImageUrl, settings.ImageToken, stream, draft.FileName);
        }
        var body = string.IsNullOrWhiteSpace(draft.Body) ? "点击查看图片" : draft.Body;
        stage?.Invoke("正在加密并推送…");
        await new BarkClient(http).SendAsync(settings.BarkUrl, settings.DeviceKey, settings.Group,
            new PushMessage(body, imageUrl, settings.NotificationLevel, settings.AutoCopy), encryptionKey: settings.EncryptionKey);
    }
}
