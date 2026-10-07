using HarLens.Core.Model;

namespace HarLens.Core.Http;

/// <summary>A request cookie or a parsed <c>Set-Cookie</c> with its attributes as columns.</summary>
public sealed class CookieInfo
{
    public string Name { get; init; } = "";

    public string Value { get; init; } = "";

    public string? Domain { get; init; }

    public string? Path { get; init; }

    public string? Expires { get; init; }

    public string? MaxAge { get; init; }

    public bool HttpOnly { get; init; }

    public bool Secure { get; init; }

    public string? SameSite { get; init; }

    public bool Partitioned { get; init; }

    /// <summary>Attributes not recognised above, as written.</summary>
    public string? Other { get; init; }
}

public static class Cookies
{
    /// <summary>Parses all <c>Cookie</c> request headers into name/value pairs.</summary>
    public static List<CookieInfo> ParseRequestCookies(IEnumerable<HarHeader> headers)
    {
        var list = new List<CookieInfo>();
        foreach (var h in headers)
        {
            if (!h.Name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var part in h.Value.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=');
                list.Add(eq < 0
                    ? new CookieInfo { Name = "", Value = trimmed }
                    : new CookieInfo { Name = trimmed[..eq].Trim(), Value = trimmed[(eq + 1)..].Trim() });
            }
        }

        return list;
    }

    /// <summary>Parses every <c>Set-Cookie</c> response header (RFC 6265 section 5.2, lenient).</summary>
    public static List<CookieInfo> ParseSetCookies(IEnumerable<HarHeader> headers)
    {
        var list = new List<CookieInfo>();
        foreach (var h in headers)
        {
            if (!h.Name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Some producers fold several cookies into one value separated by newlines.
            foreach (var line in h.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                list.Add(ParseSetCookie(line));
            }
        }

        return list;
    }

    public static CookieInfo ParseSetCookie(string value)
    {
        var parts = value.Split(';');
        var first = parts[0];
        var eq = first.IndexOf('=');
        var name = eq < 0 ? "" : first[..eq].Trim();
        var val = eq < 0 ? first.Trim() : first[(eq + 1)..].Trim();
        string? domain = null, path = null, expires = null, maxAge = null, sameSite = null;
        bool httpOnly = false, secure = false, partitioned = false;
        var other = new List<string>();
        foreach (var attr in parts.Skip(1))
        {
            var a = attr.Trim();
            if (a.Length == 0)
            {
                continue;
            }

            var aeq = a.IndexOf('=');
            var key = (aeq < 0 ? a : a[..aeq]).Trim();
            var av = aeq < 0 ? "" : a[(aeq + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "domain":
                    domain = av;
                    break;
                case "path":
                    path = av;
                    break;
                case "expires":
                    expires = av;
                    break;
                case "max-age":
                    maxAge = av;
                    break;
                case "httponly":
                    httpOnly = true;
                    break;
                case "secure":
                    secure = true;
                    break;
                case "samesite":
                    sameSite = av;
                    break;
                case "partitioned":
                    partitioned = true;
                    break;
                default:
                    other.Add(a);
                    break;
            }
        }

        return new CookieInfo
        {
            Name = name,
            Value = val,
            Domain = domain,
            Path = path,
            Expires = expires,
            MaxAge = maxAge,
            HttpOnly = httpOnly,
            Secure = secure,
            SameSite = sameSite,
            Partitioned = partitioned,
            Other = other.Count == 0 ? null : string.Join("; ", other),
        };
    }
}

/// <summary>application/x-www-form-urlencoded and query string helpers (WHATWG URL rules: '+' is a space).</summary>
public static class FormUrlEncoding
{
    public static List<HarHeader> Parse(string? text)
    {
        var list = new List<HarHeader>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }

        foreach (var part in text.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];
            list.Add(new HarHeader(Decode(name), Decode(value)));
        }

        return list;
    }

    /// <summary>Query parameters of a URL, decoded.</summary>
    public static List<HarHeader> ParseQuery(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0)
        {
            return [];
        }

        var hash = url.IndexOf('#', q);
        return Parse(hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash]);
    }

    public static string Decode(string s)
    {
        if (s.IndexOfAny(['+', '%']) < 0)
        {
            return s;
        }

        try
        {
            return Uri.UnescapeDataString(s.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return s;
        }
    }

    /// <summary>Encodes a component the way browsers encode form fields (space as '+').</summary>
    public static string Encode(string s) => Uri.EscapeDataString(s).Replace("%20", "+", StringComparison.Ordinal);

    public static string Serialize(IEnumerable<HarHeader> pairs) =>
        string.Join("&", pairs.Select(p => Encode(p.Name) + "=" + Encode(p.Value)));

    /// <summary>Replaces the query string of <paramref name="url"/>, keeping any fragment.</summary>
    public static string WithQuery(string url, string query)
    {
        var hash = url.IndexOf('#');
        var fragment = hash < 0 ? "" : url[hash..];
        var withoutFragment = hash < 0 ? url : url[..hash];
        var q = withoutFragment.IndexOf('?');
        var baseUrl = q < 0 ? withoutFragment : withoutFragment[..q];
        return query.Length == 0 ? baseUrl + fragment : baseUrl + "?" + query + fragment;
    }

    /// <summary>The raw (still encoded) query string of a URL, without '?'.</summary>
    public static string RawQuery(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0)
        {
            return "";
        }

        var hash = url.IndexOf('#', q);
        return hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash];
    }
}
