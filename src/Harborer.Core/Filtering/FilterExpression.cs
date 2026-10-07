using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Harborer.Core.Har;
using Harborer.Core.Http;
using Harborer.Core.Text;

namespace Harborer.Core.Filtering;

/// <summary>One parsed term of the filter bar.</summary>
public sealed record FilterClause(string Text, string? Key, string Value, bool Negated, Func<HarEntry, bool> Predicate)
{
    public bool Matches(HarEntry entry) => Predicate(entry) != Negated;
}

/// <summary>
/// The filter bar language, following Chrome DevTools: space-separated terms combined with AND,
/// <c>key:value</c> operators, <c>/regex/</c> against the URL, free text as a URL substring, and a leading
/// <c>-</c> to negate. Unknown keys are treated as free text, as Chrome does.
/// </summary>
public sealed class FilterExpression
{
    public static readonly IReadOnlyList<string> Keys =
    [
        "status-code", "method", "domain", "scheme", "mime-type", "larger-than", "smaller-than",
        "has-response-header", "has-request-header", "header", "is", "time-greater-than", "time-less-than",
        "resource-type", "url", "priority", "remote-address", "set-cookie-name", "set-cookie-value",
        "set-cookie-domain", "cookie-name", "cookie-value", "comment", "color", "source",
    ];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly FilterClause[] _clauses;

    private FilterExpression(string text, List<FilterClause> clauses, List<string> errors)
    {
        Text = text;
        _clauses = clauses.ToArray();
        Errors = errors;
    }

    public static FilterExpression Empty { get; } = new("", [], []);

    public string Text { get; }

    public IReadOnlyList<FilterClause> Clauses => _clauses;

    public IReadOnlyList<string> Errors { get; }

    public bool IsEmpty => _clauses.Length == 0;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Matches(HarEntry entry)
    {
        var clauses = _clauses;
        for (var i = 0; i < clauses.Length; i++)
        {
            var clause = clauses[i];
            if (clause.Predicate(entry) == clause.Negated)
            {
                return false;
            }
        }

        return true;
    }

    public static FilterExpression Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var clauses = new List<FilterClause>();
        var errors = new List<string>();
        foreach (var token in Tokenize(text))
        {
            var raw = token;
            var negated = false;
            if (raw.Length > 1 && raw[0] == '-')
            {
                negated = true;
                raw = raw[1..];
            }

            try
            {
                var clause = ParseTerm(token, raw, negated, errors);
                if (clause is not null)
                {
                    clauses.Add(clause);
                }
            }
            catch (ArgumentException ex)
            {
                errors.Add($"'{token}': {ex.Message}");
            }
        }

        return new FilterExpression(text, clauses, errors);
    }

    private static FilterClause? ParseTerm(string token, string term, bool negated, List<string> errors)
    {
        if (term.Length >= 2 && term[0] == '/' && term[^1] == '/')
        {
            var regex = new Regex(term[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
            return new FilterClause(token, null, term, negated, e => SafeIsMatch(regex, e.Url));
        }

        var colon = term.IndexOf(':');
        if (colon > 0)
        {
            var key = term[..colon].ToLowerInvariant();
            var value = Unquote(term[(colon + 1)..]);
            if (Keys.Contains(key))
            {
                if (value.Length == 0)
                {
                    errors.Add($"'{token}': missing value");
                    return null;
                }

                var predicate = BuildOperator(key, value);
                if (predicate is null)
                {
                    errors.Add($"'{token}': invalid value '{value}'");
                    return null;
                }

                return new FilterClause(token, key, value, negated, predicate);
            }
        }

        var text = Unquote(term);
        return new FilterClause(token, null, text, negated, e => e.Url.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static Func<HarEntry, bool>? BuildOperator(string key, string value)
    {
        switch (key)
        {
            case "status-code":
                return StatusPredicate(value);
            case "method":
                return e => e.Method.Equals(value, StringComparison.OrdinalIgnoreCase);
            case "domain":
                {
                    var match = Wildcard(value);
                    return e => match(e.Host);
                }

            case "scheme":
                return e => e.Scheme.Equals(value, StringComparison.OrdinalIgnoreCase);
            case "mime-type":
                {
                    var match = Wildcard(value);
                    return e => match(e.MimeTypeBase);
                }

            case "larger-than":
                {
                    var size = ParseSize(value);
                    return size is null ? null : e => e.ResponseSize >= size.Value;
                }

            case "smaller-than":
                {
                    var size = ParseSize(value);
                    return size is null ? null : e => e.ResponseSize >= 0 && e.ResponseSize < size.Value;
                }

            case "has-response-header":
                return e => e.HasResponseHeader(value);
            case "has-request-header":
                return e => e.HasRequestHeader(value);
            case "header":
                return HeaderPredicate(value);
            case "is":
                return value.ToLowerInvariant() switch
                {
                    "from-cache" => e => e.IsFromCache,
                    "failed" => e => e.IsFailed,
                    "running" => _ => false,
                    "websocket" => e => ResourceCategories.Of(e) == ResourceCategory.WebSocket,
                    "annotated" => e => e.Comment is not null || e.ColorMark is not null,
                    "redirect" => e => e.Status is >= 300 and < 400 && e.Status != 304,
                    "service-worker-initiated" or "service-worker-intercepted" => e => e.InitiatorType == "serviceworker",
                    _ => null,
                };
            case "time-greater-than":
                {
                    var ms = ParseNumber(value);
                    return ms is null ? null : e => e.TotalTime > ms.Value;
                }

            case "time-less-than":
                {
                    var ms = ParseNumber(value);
                    return ms is null ? null : e => e.TotalTime >= 0 && e.TotalTime < ms.Value;
                }

            case "resource-type":
                // Chromium's _resourceType when recorded; otherwise the derived category.
                return e => string.IsNullOrEmpty(e.ResourceType)
                    ? ResourceCategories.Label(ResourceCategories.Of(e)).Equals(value, StringComparison.OrdinalIgnoreCase) ||
                      ResourceCategories.Of(e).ToString().Equals(value, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(e.ResourceType, value, StringComparison.OrdinalIgnoreCase);
            case "url":
                return e => e.Url.Contains(value, StringComparison.OrdinalIgnoreCase);
            case "priority":
                return e => string.Equals(e.Priority, value, StringComparison.OrdinalIgnoreCase);
            case "remote-address":
                {
                    var match = Wildcard(value);
                    return e => e.ServerIPAddress is { } ip && (match(ip) || match(ip.Trim('[', ']')));
                }

            case "set-cookie-name":
                return e => Cookies.ParseSetCookies(e.ResponseHeaders).Any(c => c.Name.Equals(value, StringComparison.Ordinal));
            case "set-cookie-value":
                return e => Cookies.ParseSetCookies(e.ResponseHeaders).Any(c => c.Value.Contains(value, StringComparison.Ordinal));
            case "set-cookie-domain":
                {
                    var match = Wildcard(value.TrimStart('.'));
                    return e => Cookies.ParseSetCookies(e.ResponseHeaders).Any(c => c.Domain is { } d && match(d.TrimStart('.')));
                }

            case "cookie-name":
                return e => Cookies.ParseRequestCookies(e.RequestHeaders).Any(c => c.Name.Equals(value, StringComparison.Ordinal));
            case "cookie-value":
                return e => Cookies.ParseRequestCookies(e.RequestHeaders).Any(c => c.Value.Contains(value, StringComparison.Ordinal));
            case "comment":
                return e => e.Comment?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
            case "color":
                return e => string.Equals(e.ColorMark, value, StringComparison.OrdinalIgnoreCase);
            case "source":
                return e => (e.SourceTag ?? e.Source.DisplayName).Contains(value, StringComparison.OrdinalIgnoreCase);
            default:
                return null;
        }
    }

    private static Func<HarEntry, bool>? StatusPredicate(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.Length == 3 && char.IsAsciiDigit(v[0]) && v[1..] == "xx")
        {
            var cls = v[0] - '0';
            return e => e.Status >= cls * 100 && e.Status < (cls + 1) * 100;
        }

        if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            return e => e.Status == code;
        }

        return null;
    }

    /// <summary><c>header:name</c> (present on either side) or <c>header:name=value</c> (value contains, or wildcard match).</summary>
    private static Func<HarEntry, bool> HeaderPredicate(string value)
    {
        var eq = value.IndexOf('=');
        if (eq < 0)
        {
            return e => e.HasRequestHeader(value) || e.HasResponseHeader(value);
        }

        var name = value[..eq];
        var expected = Unquote(value[(eq + 1)..]);
        Func<string, bool> test = expected.Contains('*', StringComparison.Ordinal)
            ? Wildcard(expected)
            : [MethodImpl(MethodImplOptions.AggressiveOptimization)] (v) => v.Contains(expected, StringComparison.OrdinalIgnoreCase);
        return [MethodImpl(MethodImplOptions.AggressiveOptimization)] (e) =>
        {
            foreach (var h in e.RequestHeaders)
            {
                if (h.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && test(h.Value))
                {
                    return true;
                }
            }

            foreach (var h in e.ResponseHeaders)
            {
                if (h.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && test(h.Value))
                {
                    return true;
                }
            }

            return false;
        };
    }

    /// <summary>Case-insensitive whole-value match where <c>*</c> matches any run of characters. No regex, so it is fast on 200,000 rows.</summary>
    public static Func<string, bool> Wildcard(string pattern)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return s => s.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var segments = pattern.Split('*');
        var first = segments[0];
        var last = segments[^1];
        var middle = segments[1..^1].Where(m => m.Length > 0).ToArray();
        var minLength = segments.Sum(x => x.Length);
        return [MethodImpl(MethodImplOptions.AggressiveOptimization)] (s) =>
        {
            if (s.Length < minLength ||
                !s.StartsWith(first, StringComparison.OrdinalIgnoreCase) ||
                !s.EndsWith(last, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var position = first.Length;
            var limit = s.Length - last.Length;
            foreach (var m in middle)
            {
                var found = s.AsSpan(position, limit - position).IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return false;
                }

                position += found + m.Length;
            }

            return true;
        };
    }

    /// <summary>Sizes as in Chrome: plain bytes, or with a k (1000) or M (1,000,000) suffix.</summary>
    public static long? ParseSize(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        double multiplier = 1;
        if (v.EndsWith("kb", StringComparison.Ordinal) || v.EndsWith('k'))
        {
            multiplier = 1000;
            v = v.TrimEnd('b')[..^1];
        }
        else if (v.EndsWith("mb", StringComparison.Ordinal) || v.EndsWith('m'))
        {
            multiplier = 1_000_000;
            v = v.TrimEnd('b')[..^1];
        }
        else if (v.EndsWith('b'))
        {
            v = v[..^1];
        }

        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0
            ? (long)(n * multiplier)
            : null;
    }

    private static double? ParseNumber(string value) =>
        double.TryParse(value.Trim().TrimEnd('s', 'm'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    /// <summary>Splits on whitespace, keeping double-quoted runs and <c>/regex/</c> terms (which may contain spaces) together.</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            sb.Clear();
            var start = i;
            var isRegex = text[i] == '/' || (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '/');
            if (isRegex)
            {
                // Read to the closing slash that is followed by whitespace or the end.
                var j = text.IndexOf('/', start) + 1;
                var closed = -1;
                while (j < text.Length)
                {
                    if (text[j] == '\\')
                    {
                        j += 2;
                        continue;
                    }

                    if (text[j] == '/' && (j + 1 == text.Length || char.IsWhiteSpace(text[j + 1])))
                    {
                        closed = j;
                        break;
                    }

                    j++;
                }

                if (closed > 0)
                {
                    tokens.Add(text[start..(closed + 1)]);
                    i = closed + 1;
                    continue;
                }
            }

            var inQuotes = false;
            while (i < text.Length && (inQuotes || !char.IsWhiteSpace(text[i])))
            {
                if (text[i] == '"')
                {
                    inQuotes = !inQuotes;
                }

                sb.Append(text[i]);
                i++;
            }

            tokens.Add(sb.ToString());
        }

        return tokens;
    }
}
