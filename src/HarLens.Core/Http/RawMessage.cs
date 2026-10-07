using System.Text;
using HarLens.Core.Har;
using HarLens.Core.Model;

namespace HarLens.Core.Http;

/// <summary>Reconstructs HTTP/1.1-style message text for the Raw inspector tab (SPEC 6.3).</summary>
public static class RawMessage
{
    public const int MaxInlineBody = 2 * 1024 * 1024;

    public static string Request(HarEntry entry, DecodedBody? body, Func<string, string, string>? maskValue = null)
    {
        var sb = new StringBuilder();
        var parts = UrlParts.Split(entry.Url);
        var target = entry.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase)
            ? (parts.Host + (parts.Port > 0 ? ":" + parts.Port : ""))
            : (parts.Host.Length == 0 ? entry.Url : parts.PathAndQuery);
        sb.Append(entry.Method).Append(' ').Append(target).Append(' ').Append(Http1Version(entry.RequestHttpVersion)).Append("\r\n");
        var hasHost = entry.RequestHeaders.Any(h => h.Name.Equals("host", StringComparison.OrdinalIgnoreCase));
        if (!hasHost)
        {
            var authority = HarEntry.FindHeader(entry.RequestHeaders, ":authority") ?? entry.HostDisplay;
            if (authority.Length > 0)
            {
                sb.Append("Host: ").Append(authority).Append("\r\n");
            }
        }

        AppendHeaders(sb, entry.RequestHeaders, maskValue);
        sb.Append("\r\n");
        AppendBody(sb, body);
        return sb.ToString();
    }

    public static string Response(HarEntry entry, DecodedBody? body, Func<string, string, string>? maskValue = null)
    {
        var sb = new StringBuilder();
        if (entry.Status == 0)
        {
            sb.Append("(no response");
            if (entry.Error is not null)
            {
                sb.Append(": ").Append(entry.Error);
            }

            sb.Append(")\r\n");
            return sb.ToString();
        }

        sb.Append(Http1Version(entry.ResponseHttpVersion)).Append(' ').Append(entry.Status);
        if (entry.StatusText.Length > 0)
        {
            sb.Append(' ').Append(entry.StatusText);
        }

        sb.Append("\r\n");
        AppendHeaders(sb, entry.ResponseHeaders, maskValue);
        sb.Append("\r\n");
        AppendBody(sb, body);
        return sb.ToString();
    }

    /// <summary>Normalizes "h2", "http/2.0" and blanks to an HTTP/1.1-style version token.</summary>
    public static string Http1Version(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "HTTP/1.1";
        }

        var v = version.Trim().ToLowerInvariant();
        return v switch
        {
            "h2" or "http/2" or "http/2.0" or "h2c" => "HTTP/2",
            "h3" or "http/3" or "http/3.0" => "HTTP/3",
            "http/1.0" => "HTTP/1.0",
            "http/1.1" => "HTTP/1.1",
            _ when v.StartsWith("http/", StringComparison.Ordinal) => version.Trim().ToUpperInvariant(),
            _ => version.Trim(),
        };
    }

    private static void AppendHeaders(StringBuilder sb, IEnumerable<HarHeader> headers, Func<string, string, string>? maskValue)
    {
        foreach (var h in headers)
        {
            if (h.Name.StartsWith(':'))
            {
                continue;
            }

            sb.Append(h.Name).Append(": ").Append(maskValue is null ? h.Value : maskValue(h.Name, h.Value)).Append("\r\n");
        }
    }

    private static void AppendBody(StringBuilder sb, DecodedBody? body)
    {
        if (body is null || body.Bytes.Length == 0)
        {
            return;
        }

        if (body.Text is { } text)
        {
            sb.Append(text.Length > MaxInlineBody ? text[..MaxInlineBody] + $"\r\n[… {text.Length - MaxInlineBody:N0} more characters]" : text);
        }
        else
        {
            sb.Append($"[{body.Bytes.Length:N0} bytes of binary content: see the Hex view]");
        }
    }
}
