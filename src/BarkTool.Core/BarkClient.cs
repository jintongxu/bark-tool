using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BarkTool.Core;

public sealed record PushMessage(string Body, string? ImageUrl = null, string Level = "active", bool AutoCopy = true);

public sealed class BarkClient(HttpClient http)
{
    private static readonly JsonSerializerOptions PushJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    public async Task SendAsync(string serverUrl, string deviceKey, string group, PushMessage message,
        CancellationToken cancellationToken = default, string? encryptionKey = null)
    {
        var endpoint = EndpointRules.BarkPush(serverUrl);
        if (string.IsNullOrWhiteSpace(deviceKey) || message is null
            || message.Body is null)
            throw new ArgumentException("推送内容无效。");

        // Check serialized body bytes, not character count: JSON escaping and UTF-8
        // expansion count against the safe APNs payload budget before any request is made.
        var textBytes = JsonSerializer.SerializeToUtf8Bytes(new { body = message.Body });
        if (textBytes.Length > 3500)
            throw new ArgumentException("推送内容过长，请缩短正文。");

        if (message.Level is not ("active" or "passive" or "timeSensitive"))
            throw new ArgumentException("通知类型无效。");
        object payload;
        if (encryptionKey is not null)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
            {
                body = message.Body, group, image = message.ImageUrl, url = message.ImageUrl,
                autoCopy = message.AutoCopy ? "1" : "0", level = message.Level,
                sound = message.Level == "passive" ? null : "1107"
            }, PushJsonOptions);
            try
            {
                var encrypted = BarkEncryption.Encrypt(plaintext, encryptionKey);
                payload = new { device_key = deviceKey, ciphertext = encrypted.Ciphertext, iv = encrypted.Iv };
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
            if (JsonSerializer.SerializeToUtf8Bytes(payload, PushJsonOptions).Length > 3500)
                throw new ArgumentException("加密推送内容过长，请缩短正文。");
        }
        else
        {
            payload = new { device_key = deviceKey, body = message.Body,
                group, image = message.ImageUrl, url = message.ImageUrl, autoCopy = message.AutoCopy ? "1" : "0", level = message.Level };
        }

        try
        {
            using var response = await http.PostAsJsonAsync(endpoint, payload, PushJsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("推送失败，请检查服务配置。");

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number) || number != 200)
                throw new InvalidOperationException("推送失败，请检查服务配置。");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("推送响应无效。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("推送连接失败，请检查网络。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("推送连接失败，请检查网络。");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("推送连接失败，请检查网络。");
        }
    }
}
