using System.Net;

namespace HarLens.Net;

/// <summary>A problem found before any connection is opened: bad URL, missing file, unreadable certificate.</summary>
internal sealed class RequestPreparationException : Exception
{
    public RequestPreparationException(string message)
        : base(message)
    {
    }

    public RequestPreparationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Checks and conversions done before the first connection, so input errors never reach the network.</summary>
internal static class Preflight
{
    public static bool IsHttpScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public static bool IsHttps(Uri uri) => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public static Uri ParseUrl(string? url)
    {
        string text = url?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new RequestPreparationException("No URL to send");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsHttpScheme(uri.Scheme) || uri.Host.Length == 0)
        {
            throw new RequestPreparationException(
                $"Cannot send to '{text}': only absolute http:// and https:// URLs are supported");
        }

        return uri;
    }

    /// <summary>The URL as recorded in an exchange: absolute, escaped, without the fragment (which is never sent).</summary>
    public static string DisplayUrl(Uri uri) => uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

    public static string NormalizeMethod(string? method)
    {
        string text = method?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "GET";
        }

        try
        {
            _ = new HttpMethod(text);
        }
        catch (FormatException)
        {
            throw new RequestPreparationException($"Invalid method '{text}': a method is a single token without spaces");
        }

        return text;
    }

    /// <summary>Builds the explicit proxy. The system proxy is never consulted.</summary>
    public static WebProxy? CreateProxy(string? proxy)
    {
        string text = proxy?.Trim() ?? "";
        if (text.Length == 0)
        {
            return null;
        }

        // curl treats a proxy without a scheme as http://.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            throw new RequestPreparationException(
                $"Unsupported proxy '{proxy}': use http://, https://, socks4://, socks4a:// or socks5:// host:port");
        }

        NetworkCredential? credentials = null;
        if (uri.UserInfo.Length > 0)
        {
            string[] parts = uri.UserInfo.Split(':', 2);
            credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            uri = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        }

        return new WebProxy(uri) { BypassProxyOnLocal = false, Credentials = credentials };
    }

    public static string ResolvePath(string? path, string? baseDirectory, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new RequestPreparationException($"No file given for {purpose}");
        }

        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new RequestPreparationException(
                $"Cannot resolve the relative path '{path}' for {purpose}: no base directory is set");
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    public static byte[] ReadFile(string? path, string? baseDirectory, string purpose)
    {
        string fullPath = ResolvePath(path, baseDirectory, purpose);
        try
        {
            return File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RequestPreparationException($"File not found for {purpose}: {fullPath}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RequestPreparationException($"Cannot read {fullPath} for {purpose}: {ex.Message}", ex);
        }
    }
}
