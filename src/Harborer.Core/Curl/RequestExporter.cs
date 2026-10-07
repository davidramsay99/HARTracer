using Harborer.Core.Http;

namespace Harborer.Core.Curl;

public enum ExportFormat
{
    /// <summary>curl for bash and other POSIX shells (Chrome "Copy as cURL (bash)").</summary>
    CurlBash,

    /// <summary>curl for Windows cmd.exe (Chrome "Copy as cURL (cmd)").</summary>
    CurlCmd,

    /// <summary>curl.exe for PowerShell, with PowerShell quoting and backtick line continuation.</summary>
    CurlPowerShell,

    /// <summary>An <c>Invoke-WebRequest</c> script like Chrome's "Copy as PowerShell". Export only.</summary>
    PowerShellInvokeWebRequest,

    /// <summary>An HTTP/1.1-style request message with CRLF line endings. Export only.</summary>
    RawHttp,
}

public sealed class ExportOptions
{
    /// <summary>Export a Content-Length header found in the request. Off by default.</summary>
    public bool IncludeContentLength { get; set; }

    /// <summary>Export HTTP/2 pseudo-headers (names starting with ':'). Off by default.</summary>
    public bool IncludePseudoHeaders { get; set; }
}

public sealed class ExportResult
{
    /// <summary>The generated text. Lines end with LF, except in <see cref="ExportFormat.RawHttp"/>, which uses CRLF.</summary>
    public string Text { get; init; } = "";

    /// <summary>Anything the output cannot express exactly, or that needs a particular shell or tool version.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>Generates curl commands, an Invoke-WebRequest script or a raw HTTP message from a request.</summary>
public static class RequestExporter
{
    public static ExportResult Export(HttpRequestSpec request, ExportFormat format, ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new ExportOptions();
        return format switch
        {
            ExportFormat.CurlBash => CurlCommandWriter.Write(request, CurlDialect.Bash, options),
            ExportFormat.CurlCmd => CurlCommandWriter.Write(request, CurlDialect.Cmd, options),
            ExportFormat.CurlPowerShell => CurlCommandWriter.Write(request, CurlDialect.PowerShell, options),
            ExportFormat.PowerShellInvokeWebRequest => InvokeWebRequestWriter.Write(request, options),
            ExportFormat.RawHttp => RawHttpWriter.Write(request, options),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format."),
        };
    }

    /// <summary>Enabled headers minus pseudo-headers and Content-Length unless the options ask for them.</summary>
    internal static IEnumerable<HeaderEntry> ExportedHeaders(HttpRequestSpec request, ExportOptions options) =>
        request.Headers.Where(h => h.Enabled &&
            (options.IncludePseudoHeaders || !h.Name.StartsWith(':')) &&
            (options.IncludeContentLength || !h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Splits an absolute URL into its authority and its origin-form target (path and query, no fragment).</summary>
    internal static (string Authority, string Target) SplitUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            var noFragment = url.Split('#')[0];
            return ("", noFragment.Length == 0 ? "/" : noFragment);
        }

        var start = schemeEnd + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        var authority = end < 0 ? url[start..] : url[start..end];
        var rest = end < 0 ? "" : url[end..];
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            rest = rest[..hash];
        }

        if (rest.Length == 0 || rest[0] == '?')
        {
            rest = "/" + rest;
        }

        return (authority, rest);
    }

    /// <summary>The authority without user information, as sent in the Host header.</summary>
    internal static string HostFromAuthority(string authority)
    {
        var at = authority.LastIndexOf('@');
        return at < 0 ? authority : authority[(at + 1)..];
    }
}
