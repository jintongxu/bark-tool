using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BarkTool.Desktop;

internal static class ClipboardSenderTests
{
    internal static async Task Run()
    {
        var settings = new AppSettings("https://bark.example", "test-device", "https://images.example", "test-token", EncryptionKey: "1234567890123456");
        var requests = new List<(string Path, string Body)>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync()));
            return Json(request.RequestUri.AbsolutePath.EndsWith("index.php")
                ? "{\"code\":200,\"url\":\"https://images.example/test.png\"}" : "{\"code\":200}");
        }));
        var sender = new ClipboardSender(http, () => new ClipboardContent(Text: "内容\n第二行"));
        await sender.SendAsync(settings);
        Check(!sender.IsSending && requests.Count == 1, "text direct send completes");
        using (var json = Decrypt(requests[0].Body, settings.EncryptionKey))
        {
            Check(json.RootElement.GetProperty("body").GetString() == "内容\n第二行", "text preserved");
            Check(!json.RootElement.TryGetProperty("title", out _), "no title");
        }
        requests.Clear();
        await new ClipboardSender(http, () => new ClipboardContent(Image: [1, 2, 3])).SendAsync(settings);
        Check(requests.Count == 2 && requests[0].Path == "/api/index.php" && requests[1].Path == "/push", "image upload before push");
        using (var json = Decrypt(requests[1].Body, settings.EncryptionKey))
            Check(json.RootElement.GetProperty("image").GetString() == "https://images.example/test.png", "image linked");
        requests.Clear();
        foreach (var content in new[] { new ClipboardContent(), new ClipboardContent(Text: "  "), new ClipboardContent(Image: new byte[10 * 1024 * 1024 + 1]) })
        {
            var invalid = new ClipboardSender(http, () => content);
            await Reject(() => invalid.SendAsync(settings));
            Check(!invalid.IsSending, "failure resets busy");
        }
        await Reject(() => new ClipboardSender(http, () => new ClipboardContent(Image: [1])).SendAsync(settings with { ImageToken = "" }));
        await Reject(() => new ClipboardSender(http, () => new ClipboardContent(Text: "private")).SendAsync(settings with { EncryptionKey = "" }));
        Check(requests.Count == 0, "invalid contents or missing encryption key do not send");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blockedHttp = new HttpClient(new Handler(_ => pending.Task));
        var busy = new ClipboardSender(blockedHttp, () => new ClipboardContent(Text: "text"));
        var sending = busy.SendAsync(settings);
        Check(busy.IsSending, "busy while request pending");
        await Reject(() => busy.SendAsync(settings));
        pending.SetResult(Json("{\"code\":400}"));
        await Reject(() => sending);
        Check(!busy.IsSending, "network failure resets busy");
        requests.Clear();
        var coordinator = new SendCoordinator(http, () => settings);
        await new ClipboardSender(http, () => new ClipboardContent(Text: "integrated text"), coordinator).SendAsync(settings);
        Check(requests.Count == 1, "clipboard stateless coordinator sends once");
        using (var text = Decrypt(requests.Single().Body, settings.EncryptionKey))
            Check(text.RootElement.GetProperty("body").GetString() == "integrated text", "coordinator encrypts body");
        requests.Clear();
        await coordinator.SendAsync(new SendDraft("", [1, 2, 3], "a.png"));
        Check(requests.Count == 2, "stateless image uploads once and pushes once");
        Console.WriteLine("PASS direct clipboard text/image, stateless coordinator, validation, failure and duplicate-send prevention");
    }
    private static JsonDocument Decrypt(string request, string key)
    {
        using var outer = JsonDocument.Parse(request);
        Check(!outer.RootElement.TryGetProperty("body", out _), "no plaintext body on wire");
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        return JsonDocument.Parse(aes.DecryptCbc(Convert.FromBase64String(outer.RootElement.GetProperty("ciphertext").GetString()!),
            Encoding.ASCII.GetBytes(outer.RootElement.GetProperty("iv").GetString()!)));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new Exception("Expected rejection");
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
}
