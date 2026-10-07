using System.Text.RegularExpressions;

namespace HarLens.Core.Sanitize;

/// <summary>
/// "Mask secrets in UI" (SPEC 8): replaces sensitive values with <c>••••</c> plus the last four characters, for
/// screen sharing. Copy operations use the same functions so that the clipboard honors the mask.
/// </summary>
public static class SecretMasker
{
    public const string Bullets = "••••";

    private static readonly Regex JwtRegex = new(@"\beyJ[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex AuthRegex = new(@"\b(?<scheme>Bearer|Basic)\s+(?<secret>[A-Za-z0-9\-._~+/]{6,}=*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex ParamRegex = BuildParamRegex(SanitizeOptions.DefaultParameterNames);

    private static readonly HashSet<string> SensitiveHeaders = new(SanitizeOptions.DefaultHeaderNames, StringComparer.OrdinalIgnoreCase);

    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Length <= 4 ? Bullets : Bullets + value[^4..];
    }

    public static bool IsSensitiveHeader(string name) => SensitiveHeaders.Contains(name);

    /// <summary>Masks a header value: the whole value for sensitive headers (keeping the scheme and cookie names), patterns for others.</summary>
    public static string MaskHeaderValue(string name, string value)
    {
        if (!IsSensitiveHeader(name))
        {
            return MaskText(value);
        }

        var lower = name.ToLowerInvariant();
        if (lower is "authorization" or "proxy-authorization")
        {
            var space = value.IndexOf(' ');
            return space > 0 ? value[..space] + " " + Mask(value[(space + 1)..]) : Mask(value);
        }

        if (lower is "cookie")
        {
            return string.Join("; ", value.Split(';').Select(p =>
            {
                var eq = p.IndexOf('=');
                return eq < 0 ? p.Trim() : p[..eq].Trim() + "=" + Mask(p[(eq + 1)..].Trim());
            }));
        }

        if (lower is "set-cookie")
        {
            var semi = value.IndexOf(';');
            var pair = semi < 0 ? value : value[..semi];
            var eq = pair.IndexOf('=');
            return eq < 0 ? Mask(value) : pair[..eq] + "=" + Mask(pair[(eq + 1)..]) + (semi < 0 ? "" : value[semi..]);
        }

        return Mask(value);
    }

    /// <summary>Masks JWTs, Bearer and Basic credentials, and sensitive query or form parameters inside free text.</summary>
    public static string MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        try
        {
            var result = text;
            if (result.Contains("eyJ", StringComparison.Ordinal))
            {
                result = JwtRegex.Replace(result, m => Mask(m.Value));
            }

            if (result.Contains("earer", StringComparison.OrdinalIgnoreCase) || result.Contains("asic", StringComparison.OrdinalIgnoreCase))
            {
                result = AuthRegex.Replace(result, m => m.Groups["scheme"].Value + " " + Mask(m.Groups["secret"].Value));
            }

            if (result.Contains('=', StringComparison.Ordinal))
            {
                result = ParamRegex.Replace(result, m => m.Groups["name"].Value + "=" + Mask(m.Groups["secret"].Value));
            }

            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    private static Regex BuildParamRegex(IEnumerable<string> names) =>
        new($@"(?<=^|[?&#;\s""'])(?<name>{string.Join("|", names.OrderByDescending(n => n.Length).Select(Regex.Escape))})=(?<secret>[^&#\s""'<>;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
