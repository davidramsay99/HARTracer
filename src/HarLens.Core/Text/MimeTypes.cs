namespace HarLens.Core.Text;

public enum MimeCategory
{
    Document,
    Script,
    Stylesheet,
    Image,
    Font,
    Media,
    Json,
    Xml,
    Text,
    Binary,
    Other,
}

public static class MimeTypes
{
    public static string StripParameters(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
        {
            return "";
        }

        var semi = mimeType.IndexOf(';');
        var core = semi < 0 ? mimeType : mimeType[..semi];
        core = core.Trim();
        return IsLowerAscii(core) ? core : core.ToLowerInvariant();
    }

    public static string? GetParameter(string? mimeType, string name)
    {
        if (string.IsNullOrEmpty(mimeType))
        {
            return null;
        }

        foreach (var part in mimeType.Split(';').Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    public static bool IsJson(string? mimeType)
    {
        var m = StripParameters(mimeType);
        return m is "application/json" or "text/json" or "application/x-json" or "application/ld+json" or "application/manifest+json" ||
               m.EndsWith("+json", StringComparison.Ordinal) || m.EndsWith("/json", StringComparison.Ordinal);
    }

    public static bool IsXml(string? mimeType)
    {
        var m = StripParameters(mimeType);
        return m is "application/xml" or "text/xml" || m.EndsWith("+xml", StringComparison.Ordinal);
    }

    public static bool IsHtml(string? mimeType)
    {
        var m = StripParameters(mimeType);
        return m is "text/html" or "application/xhtml+xml";
    }

    public static bool IsJavaScript(string? mimeType)
    {
        var m = StripParameters(mimeType);
        return m is "application/javascript" or "text/javascript" or "application/x-javascript" or "application/ecmascript" or "text/ecmascript";
    }

    public static bool IsCss(string? mimeType) => StripParameters(mimeType) == "text/css";

    public static bool IsFormUrlEncoded(string? mimeType) => StripParameters(mimeType) == "application/x-www-form-urlencoded";

    public static bool IsMultipart(string? mimeType) => StripParameters(mimeType).StartsWith("multipart/", StringComparison.Ordinal);

    public static bool IsImage(string? mimeType) => StripParameters(mimeType).StartsWith("image/", StringComparison.Ordinal);

    /// <summary>True for MIME types whose bodies are text (base64 bodies are decoded before searching when textual).</summary>
    public static bool IsTextual(string? mimeType)
    {
        var m = StripParameters(mimeType);
        if (m.Length == 0)
        {
            return false;
        }

        return m.StartsWith("text/", StringComparison.Ordinal) || IsJson(m) || IsXml(m) || IsJavaScript(m) ||
               m is "application/x-www-form-urlencoded" or "application/graphql" or "application/x-ndjson" or
                   "application/x-yaml" or "application/yaml" or "application/sql" or "application/csp-report" or
                   "application/x-sh" or "application/soap+xml" or "image/svg+xml" or "application/x-protobuf+json" ||
               m.EndsWith("+yaml", StringComparison.Ordinal);
    }

    public static MimeCategory Categorize(string? mimeType)
    {
        var m = StripParameters(mimeType);
        if (m.Length == 0)
        {
            return MimeCategory.Other;
        }

        if (IsHtml(m))
        {
            return MimeCategory.Document;
        }

        if (IsJavaScript(m))
        {
            return MimeCategory.Script;
        }

        if (IsCss(m))
        {
            return MimeCategory.Stylesheet;
        }

        if (m.StartsWith("image/", StringComparison.Ordinal))
        {
            return MimeCategory.Image;
        }

        if (m.StartsWith("font/", StringComparison.Ordinal) || m.Contains("font", StringComparison.Ordinal))
        {
            return MimeCategory.Font;
        }

        if (m.StartsWith("audio/", StringComparison.Ordinal) || m.StartsWith("video/", StringComparison.Ordinal) ||
            m is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "application/dash+xml")
        {
            return MimeCategory.Media;
        }

        if (IsJson(m))
        {
            return MimeCategory.Json;
        }

        if (IsXml(m))
        {
            return MimeCategory.Xml;
        }

        if (IsTextual(m))
        {
            return MimeCategory.Text;
        }

        return m is "application/octet-stream" or "application/zip" or "application/pdf" or "application/wasm"
            ? MimeCategory.Binary
            : MimeCategory.Other;
    }

    private static bool IsLowerAscii(string s)
    {
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z')
            {
                return false;
            }
        }

        return true;
    }
}
