using System.Net;
using System.Net.Sockets;

namespace BarkTool.Core;

public static class EndpointRules
{
    public static Uri BarkPush(string value) => AppendPath(Parse(value), "push");

    public static Uri EasyImageUpload(string value)
    {
        var endpoint = Parse(value);
        return endpoint.AbsolutePath.EndsWith("/api/index.php", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : AppendPath(endpoint, "api/index.php");
    }

    private static Uri AppendPath(Uri endpoint, string suffix)
    {
        var builder = new UriBuilder(endpoint);
        builder.Path = endpoint.AbsolutePath.TrimEnd('/') + "/" + suffix;
        return builder.Uri;
    }

    private static Uri Parse(string value)
    {
        // Do not allow URL parsing to normalize away an embedded query, fragment, or credentials.
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('?') || value.Contains('#') || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.Host.Length == 0
            || endpoint.AbsolutePath.Contains('\\'))
            throw new ArgumentException("服务地址无效。");

        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var originalHost = authority.StartsWith('[') ? authority.Split(']')[0] + "]" : authority.Split(':')[0];
        if (endpoint.Scheme == Uri.UriSchemeHttp && !IsDevelopmentHost(originalHost))
            throw new ArgumentException("服务地址必须使用 HTTPS。");

        return endpoint;
    }

    private static bool IsDevelopmentHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || !IsLiteralIPv4(host)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsLiteralIPv4(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(part => part.Length is >= 1 and <= 3
            && part.All(c => c is >= '0' and <= '9')
            && byte.TryParse(part, out _));
    }
}
