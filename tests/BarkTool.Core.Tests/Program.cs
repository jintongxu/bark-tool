using System.Net;
using System.Text;
using System.Text.Json;
using BarkTool.Core;

var failures = new List<string>();
await Run("endpoints and protocol", async () =>
{
    Equal("https://host.example/base/push", EndpointRules.BarkPush("https://host.example/base/").AbsoluteUri);
    Equal("https://host.example/base/api/index.php", EndpointRules.EasyImageUpload("https://host.example/base/").AbsoluteUri);
    Equal("https://host.example/base/api/index.php", EndpointRules.EasyImageUpload("https://host.example/base/api/index.php").AbsoluteUri);
    Equal("http://localhost:8080/push", EndpointRules.BarkPush("http://localhost:8080").AbsoluteUri);
    Equal("http://192.168.1.2/api/index.php", EndpointRules.EasyImageUpload("http://192.168.1.2").AbsoluteUri);
    foreach (var value in new[] { "http://example.com", "http://127.0.0.1", "http://172.32.0.1", "http://0300.0250.1.2", "http://3232235778", "ftp://example.com", "https://user:secret@example.com", "https://example.com/?secret=1", "https://example.com/#fragment", "not a URL" })
        await ThrowsSafe(() => Task.FromResult(EndpointRules.BarkPush(value)), "secret");
});
await Run("push payload and application response", async () =>
{
    var fake = new FakeHandler((_, _) => JsonResponse("{\"code\":200}"));
    using var http = new HttpClient(fake);
    await new BarkClient(http).SendAsync("https://example.com/sub/", "private-device", "daily", new PushMessage("内容", "https://image.example/p.png"));
    Equal("https://example.com/sub/push", fake.Uri);
    using var json = JsonDocument.Parse(fake.Content!);
    Equal("private-device", json.RootElement.GetProperty("device_key").GetString());
    Equal("daily", json.RootElement.GetProperty("group").GetString());
    Equal("1", json.RootElement.GetProperty("autoCopy").GetString());
    Equal("https://image.example/p.png", json.RootElement.GetProperty("image").GetString());
    Equal("https://image.example/p.png", json.RootElement.GetProperty("url").GetString());
    Check(!json.RootElement.TryGetProperty("title", out _));
    Equal("内容", json.RootElement.GetProperty("body").GetString());
    Equal(1, fake.Calls);
    await ThrowsSafe(() => new BarkClient(http).SendAsync("https://example.com", "private-device", "g", new PushMessage(new string('中', 1200))), "private-device");
    Equal(1, fake.Calls);
    await new BarkClient(http).SendAsync("https://example.com", "private-device", "g", new PushMessage("仅正文\n第二行"));
    using var textOnly = JsonDocument.Parse(fake.Content!);
    Equal("1", textOnly.RootElement.GetProperty("autoCopy").GetString());
    Check(!textOnly.RootElement.TryGetProperty("copy", out _));
    Check(!textOnly.RootElement.TryGetProperty("title", out _));
    Check(!textOnly.RootElement.TryGetProperty("image", out _));
    Check(!textOnly.RootElement.TryGetProperty("url", out _));
    Check(textOnly.RootElement.EnumerateObject().All(property => property.Value.ValueKind != JsonValueKind.Null));
    Equal("仅正文\n第二行", textOnly.RootElement.GetProperty("body").GetString());
    await new BarkClient(http).SendAsync("https://example.com", "private-device", "g", new PushMessage("单行正文"));
    using var singleLine = JsonDocument.Parse(fake.Content!);
    Equal("单行正文", singleLine.RootElement.GetProperty("body").GetString());
    Check(!singleLine.RootElement.TryGetProperty("image", out _));
    Check(!singleLine.RootElement.TryGetProperty("url", out _));
});
await Run("push failure modes", async () =>
{
    foreach (var body in new[] { "{\"code\":400,\"message\":\"private-device\"}", "{}", "no-json" })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => JsonResponse(body)));
        await ThrowsSafe(() => new BarkClient(http).SendAsync("https://example.com", "private-device", "g", new PushMessage("b")), "private-device");
    }
    using var badHttp = new HttpClient(new FakeHandler((_, _) => JsonResponse("private-device", HttpStatusCode.BadRequest)));
    await ThrowsSafe(() => new BarkClient(badHttp).SendAsync("https://example.com", "private-device", "g", new PushMessage("b")), "private-device");
    using var transport = new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("private-device")));
    await ThrowsSafe(() => new BarkClient(transport).SendAsync("https://example.com", "private-device", "g", new PushMessage("b")), "private-device");
});
await Run("upload multipart and response", async () =>
{
    var fake = new FakeHandler((_, _) => JsonResponse("{\"code\":200,\"url\":\"https://cdn.example/image.png\"}"));
    using var http = new HttpClient(fake);
    var result = await new EasyImageClient(http).UploadAsync("https://example.com/sub/", "private-token", new MemoryStream([1, 2, 3]), "image.png");
    Equal("https://cdn.example/image.png", result);
    Equal("https://example.com/sub/api/index.php", fake.Uri);
    Check(fake.Content!.Contains("name=token", StringComparison.Ordinal));
    Check(fake.Content.Contains("private-token", StringComparison.Ordinal));
    Check(fake.Content.Contains("name=image; filename=image.png", StringComparison.Ordinal));
    Check(fake.Content.Contains("\u0001\u0002\u0003", StringComparison.Ordinal));
    using var second = new HttpClient(new FakeHandler((_, _) => JsonResponse("{\"code\":200,\"url\":\"http://localhost/image.png\"}")));
    Equal("http://localhost/image.png", await new EasyImageClient(second).UploadAsync("https://example.com/sub/api/index.php", "token", new MemoryStream([1]), "a.png"));
});
await Run("upload failure modes", async () =>
{
    foreach (var body in new[] { "{\"code\":400,\"url\":\"https://cdn.example/secret\"}", "{\"code\":200}", "{\"code\":200,\"url\":\"relative\"}", "{\"code\":200,\"url\":\"https://user:pass@cdn.example/x\"}", "broken" })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => JsonResponse(body)));
        await ThrowsSafe(() => new EasyImageClient(http).UploadAsync("https://example.com", "private-token", new MemoryStream([1]), "a.png"), "private-token", "secret", "cdn.example");
    }
    using var badHttp = new HttpClient(new FakeHandler((_, _) => JsonResponse("private-token", HttpStatusCode.Forbidden)));
    await ThrowsSafe(() => new EasyImageClient(badHttp).UploadAsync("https://example.com", "private-token", new MemoryStream([1]), "a.png"), "private-token");
    var fake = new FakeHandler((_, _) => JsonResponse("{\"code\":200}"));
    using var limitHttp = new HttpClient(fake);
    await ThrowsSafe(() => new EasyImageClient(limitHttp).UploadAsync("https://example.com", "private-token", new MemoryStream(new byte[10 * 1024 * 1024 + 1]), "a.png"), "private-token");
    Equal(0, fake.Calls);
});
await Run("encrypted push compatibility", async () =>
{
    var fake = new FakeHandler((_, _) => JsonResponse("{\"code\":200}"));
    using var http = new HttpClient(fake);
    string? previousIv = null;
    foreach (var key in new[] { "1234567890123456", "123456789012345678901234", "12345678901234567890123456789012" })
    {
        foreach (var image in new string?[] { null, "https://image.example/private.png" })
        {
            await new BarkClient(http).SendAsync("https://example.com", "device", "private-group", new PushMessage("私密正文\n第二行", image), encryptionKey: key);
            using var outer = JsonDocument.Parse(fake.Content!);
            Equal(3, outer.RootElement.EnumerateObject().Count());
            Check(!fake.Content!.Contains("private-group"));
            var iv = outer.RootElement.GetProperty("iv").GetString()!;
            Check(iv.Length == 16 && iv != previousIv);
            previousIv = iv;
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key);
            var plain = aes.DecryptCbc(Convert.FromBase64String(outer.RootElement.GetProperty("ciphertext").GetString()!), Encoding.UTF8.GetBytes(iv));
            using var inner = JsonDocument.Parse(plain);
            Equal("私密正文\n第二行", inner.RootElement.GetProperty("body").GetString());
            Equal("1", inner.RootElement.GetProperty("autoCopy").GetString());
            Check(!inner.RootElement.TryGetProperty("title", out _));
            if (image is null) Check(!inner.RootElement.TryGetProperty("image", out _));
            else Equal(image, inner.RootElement.GetProperty("image").GetString());
        }
    }
    var before = fake.Calls;
    await ThrowsSafe(() => new BarkClient(http).SendAsync("https://example.com", "device", "g", new PushMessage("body"), encryptionKey: "bad-secret"), "bad-secret");
    await ThrowsSafe(() => new BarkClient(http).SendAsync("https://example.com", "device", "g", new PushMessage(new string('a', 3000)), encryptionKey: "1234567890123456"));
    Equal(before, fake.Calls);
});
await Run("notification options encrypted", async () =>
{
    var handler = new FakeHandler((_, _) => JsonResponse("{\"code\":200}"));
    using var http = new HttpClient(handler);
    foreach (var level in new[] { "active", "passive", "timeSensitive" })
    {
        await new BarkClient(http).SendAsync("https://example.com", "key", "g", new PushMessage("message", Level: level, AutoCopy: false), encryptionKey: "1234567890123456");
        using var outer = JsonDocument.Parse(handler.Content!);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes("1234567890123456");
        using var inner = JsonDocument.Parse(aes.DecryptCbc(Convert.FromBase64String(outer.RootElement.GetProperty("ciphertext").GetString()!), Encoding.UTF8.GetBytes(outer.RootElement.GetProperty("iv").GetString()!)));
        Equal(level, inner.RootElement.GetProperty("level").GetString());
        Equal("0", inner.RootElement.GetProperty("autoCopy").GetString());
        Equal(level != "passive", inner.RootElement.TryGetProperty("sound", out _));
    }
});
Console.WriteLine(failures.Count == 0 ? "All core tests passed." : string.Join(Environment.NewLine, failures));
return failures.Count == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures.Add($"FAIL {name}: {e.GetType().Name} at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
}
static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Assertion failed"); }
static async Task ThrowsSafe(Func<Task> action, params string[] secrets)
{
    try { await action(); } catch (Exception e)
    {
        Check(e is ArgumentException or InvalidOperationException);
        Check(e.Message.Any(c => c > 127));
        foreach (var secret in secrets) Check(!e.Message.Contains(secret, StringComparison.OrdinalIgnoreCase));
        Check(e.InnerException is null);
        return;
    }
    throw new Exception("Expected exception");
}
static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string? Uri { get; private set; }
    public string? Content { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Uri = request.RequestUri?.AbsoluteUri;
        Content = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request, cancellationToken);
    }
}
