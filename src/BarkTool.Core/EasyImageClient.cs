using System.Net.Http.Headers;
using System.Text.Json;

namespace BarkTool.Core;

public sealed class EasyImageClient(HttpClient http)
{
    public async Task<string> UploadAsync(string baseUrl, string token, Stream image, string fileName,
        CancellationToken cancellationToken = default)
    {
        var endpoint = EndpointRules.EasyImageUpload(baseUrl);
        if (string.IsNullOrWhiteSpace(token) || image is null || !image.CanRead
            || string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("图片上传参数无效。");
        if (image.CanSeek && image.Length - image.Position > 10 * 1024 * 1024)
            throw new ArgumentException("图片不能超过 10 MiB。");

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(token), "token");
            using var imageContent = new StreamContent(image);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(imageContent, "image", Path.GetFileName(fileName));
            using var response = await http.PostAsync(endpoint, form, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("图片上传失败，请检查服务配置。");
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number
                || !code.TryGetInt32(out var number) || number != 200
                || !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var imageUrl)
                || (imageUrl.Scheme != Uri.UriSchemeHttp && imageUrl.Scheme != Uri.UriSchemeHttps)
                || imageUrl.UserInfo.Length != 0)
                throw new InvalidOperationException("图片上传响应无效。");
            return imageUrl.AbsoluteUri;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("图片上传响应无效。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("图片上传连接失败，请检查网络。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("图片上传连接失败，请检查网络。");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("图片上传连接失败，请检查网络。");
        }
    }
}
