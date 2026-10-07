namespace HarLens.Core.Http;

/// <summary>Immutable (safe to publish across threads) URL splitting for list columns. Tolerates the odd URLs HAR files contain (data:, blob:, chrome-extension:).</summary>
public sealed record UrlParts(string Scheme, string Host, int Port, string PathAndQuery, string Query)
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

    /// <summary>
    /// Scheme and host (both lower case) and explicit port, parsed over spans and interned through
    /// <paramref name="pool"/> so that indexing allocates nothing for repeated hosts.
    /// </summary>
    internal static (string Scheme, string Host, int Port) SplitAuthority(string url, Har.StringPool? pool)
    {
        var span = url.AsSpan();
        var colon = span.IndexOf(':');
        if (colon <= 0 || !IsScheme(span[..colon]))
        {
            return ("", "", -1);
        }

        var scheme = Intern(span[..colon], pool);
        if (span.Length < colon + 3 || span[colon + 1] != '/' || span[colon + 2] != '/')
        {
            return (scheme, "", -1);
        }

        var rest = span[(colon + 3)..];
        var end = rest.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
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
                if (close + 2 < authority.Length && authority[close + 1] == ':' &&
                    !int.TryParse(authority[(close + 2)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port))
                {
                    port = -1;
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

        return (scheme, Intern(host, pool), port);
    }

    private static string Intern(ReadOnlySpan<char> value, Har.StringPool? pool)
    {
        Span<char> lower = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        value.ToLowerInvariant(lower);
        return pool is null ? new string(lower) : pool.Get(lower);
    }

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
