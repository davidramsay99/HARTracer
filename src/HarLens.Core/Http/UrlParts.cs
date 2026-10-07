namespace HarLens.Core.Http;

/// <summary>Fast, allocation-light URL splitting for list columns. Tolerates the odd URLs HAR files contain (data:, blob:, chrome-extension:).</summary>
public readonly record struct UrlParts(string Scheme, string Host, int Port, string PathAndQuery, string Query)
{
    public static bool IsDefaultPort(string scheme, int port) => (scheme, port) switch
    {
        ("http", 80) or ("ws", 80) or ("https", 443) or ("wss", 443) => true,
        _ => false,
    };

    public static int DefaultPort(string scheme) => scheme switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        _ => -1,
    };

    public static UrlParts Split(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return new UrlParts("", "", -1, "", "");
        }

        var colon = url.IndexOf(':');
        if (colon <= 0 || !IsScheme(url.AsSpan(0, colon)))
        {
            return new UrlParts("", "", -1, url, QueryOf(url));
        }

        var scheme = url[..colon].ToLowerInvariant();
        if (url.Length < colon + 3 || url[colon + 1] != '/' || url[colon + 2] != '/')
        {
            // data:, blob:, about: and similar.
            return new UrlParts(scheme, "", -1, url[(colon + 1)..], "");
        }

        var authorityStart = colon + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        var authority = url.AsSpan(authorityStart, authorityEnd - authorityStart);
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        var host = authority;
        var port = -1;
        if (authority.StartsWith("["))
        {
            var close = authority.IndexOf(']');
            if (close > 0)
            {
                host = authority[..(close + 1)];
                if (close + 1 < authority.Length && authority[close + 1] == ':')
                {
                    _ = int.TryParse(authority[(close + 2)..], out port);
                }
            }
        }
        else
        {
            var portColon = authority.LastIndexOf(':');
            if (portColon >= 0)
            {
                host = authority[..portColon];
                if (!int.TryParse(authority[(portColon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port))
                {
                    port = -1;
                }
            }
        }

        var hashIndex = url.IndexOf('#', authorityEnd);
        var pathEnd = hashIndex < 0 ? url.Length : hashIndex;
        var path = authorityEnd < pathEnd ? url[authorityEnd..pathEnd] : "/";
        if (path.Length > 0 && path[0] == '?')
        {
            path = "/" + path;
        }

        return new UrlParts(scheme, host.ToString().ToLowerInvariant(), port, path, QueryOf(path));
    }

    private static string QueryOf(string pathAndQuery)
    {
        var q = pathAndQuery.IndexOf('?');
        if (q < 0)
        {
            return "";
        }

        var hash = pathAndQuery.IndexOf('#', q);
        return hash < 0 ? pathAndQuery[(q + 1)..] : pathAndQuery[(q + 1)..hash];
    }

    private static bool IsScheme(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || !char.IsAsciiLetter(s[0]))
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }
}
