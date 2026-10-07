using System.Globalization;
using HarLens.Core.Http;

namespace HarLens.Core.Curl;

/// <summary>
/// Writes an <c>Invoke-WebRequest</c> script in the shape of Chrome's "Copy as PowerShell": a WebRequestSession
/// carrying User-Agent and cookies, then the call with <c>-Headers</c>, <c>-ContentType</c> and <c>-Body</c>.
/// Strings use Chrome's escaping (backtick before <c>` $ "</c>, <c>$([char]N)</c> outside printable ASCII).
/// Request options map to Invoke-WebRequest parameters where one exists.
/// </summary>
internal static class InvokeWebRequestWriter
{
    /// <summary>Headers Invoke-WebRequest sets itself or takes through another parameter (Chrome's list).</summary>
    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "proxy-connection", "content-length", "expect", "range", "content-type", "user-agent", "cookie",
    };

    private static string Q(string value) => ShellQuoting.PowerShellChromeStyle(value);

    public static ExportResult Write(HttpRequestSpec request, ExportOptions options)
    {
        var warnings = new List<string>();
        var needsPowerShell7 = new List<string>();
        var headers = RequestExporter.ExportedHeaders(request, options).ToList();
        var o = request.Options;

        var session = new List<string>();
        var userAgent = headers.FirstOrDefault(h => h.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase));
        if (userAgent is not null)
        {
            session.Add("$session.UserAgent = " + Q(userAgent.Value));
        }

        var cookieHeaders = headers.Where(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).ToList();
        var cookieInHeaders = false;
        if (cookieHeaders.Count > 0)
        {
            var cookies = ParseCookies(string.Join("; ", cookieHeaders.Select(h => h.Value)));
            if (cookies is null)
            {
                // System.Net.Cookie rejects some values; send the header as written instead.
                cookieInHeaders = true;
            }
            else
            {
                var (authority, _) = RequestExporter.SplitUrl(request.Url);
                var domain = DomainOf(RequestExporter.HostFromAuthority(authority));
                foreach (var (name, value) in cookies)
                {
                    session.Add($"$session.Cookies.Add((New-Object System.Net.Cookie({Q(name)}, {Q(value)}, \"/\", {Q(domain)})))");
                }
            }
        }

        var command = new List<string> { "-Uri " + Q(request.Url) };
        if (request.Method != "GET")
        {
            command.Add("-Method " + Q(request.Method));
        }

        if (session.Count > 0)
        {
            command.Add("-WebSession $session");
        }

        var dropped = new List<string>();
        var pairs = new List<(string Name, string Value)>();
        foreach (var header in headers)
        {
            var name = header.Name.TrimStart(':');
            var keepCookie = cookieInHeaders && name.Equals("cookie", StringComparison.OrdinalIgnoreCase);
            if (ManagedHeaders.Contains(name) && !keepCookie)
            {
                if (name.ToLowerInvariant() is not ("content-type" or "user-agent" or "cookie"))
                {
                    dropped.Add(header.Name);
                }

                continue;
            }

            var existing = pairs.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                warnings.Add($"PowerShell hashtables cannot repeat a key; the values of header '{pairs[existing].Name}' were joined with ', '.");
                pairs[existing] = (pairs[existing].Name, pairs[existing].Value + ", " + header.Value);
            }
            else
            {
                pairs.Add((name, header.Value));
            }
        }

        if (dropped.Count > 0)
        {
            warnings.Add("Invoke-WebRequest sets these headers itself; they were left out: " + string.Join(", ", dropped));
        }

        if (pairs.Count > 0)
        {
            command.Add("-Headers @{\n" + string.Join("\n  ", pairs.Select(p => Q(p.Name) + "=" + Q(p.Value))) + "\n}");
        }

        var body = request.Body;
        var parts = body.Mode == BodyMode.Multipart ? body.Parts.Where(p => p.Enabled).ToList() : [];
        var contentType = headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        if (contentType is not null && parts.Count == 0)
        {
            command.Add("-ContentType " + Q(contentType.Value));
        }

        AddBody(body, parts, command, warnings, needsPowerShell7);
        AddOptions(request, o, command, warnings, needsPowerShell7);

        if (needsPowerShell7.Count > 0)
        {
            warnings.Add("These parameters need PowerShell 7 or later: " + string.Join(", ", needsPowerShell7.Distinct()));
        }

        var prelude = session.Count > 0
            ? "$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession\n" + string.Join("\n", session) + "\n"
            : "";
        var text = prelude + "Invoke-WebRequest -UseBasicParsing " + string.Join(command.Count >= 3 ? " `\n" : " ", command);
        var result = new ExportResult { Text = text };
        result.Warnings.AddRange(warnings);
        return result;
    }

    private static void AddBody(RequestBody body, List<MultipartPart> parts, List<string> command, List<string> warnings, List<string> needsPowerShell7)
    {
        switch (body.Mode)
        {
            case BodyMode.None:
                return;
            case BodyMode.BinaryFile:
                if (!string.IsNullOrEmpty(body.FilePath))
                {
                    command.Add("-InFile " + Q(body.FilePath));
                }

                return;
            case BodyMode.Multipart:
                if (parts.Count == 0)
                {
                    return;
                }

                var fields = new List<(string Name, List<string> Values)>();
                foreach (var part in parts)
                {
                    var value = part.FilePath is null
                        ? Q(part.Value ?? "")
                        : part.FileContentAsValue
                            ? "(Get-Content -Raw -LiteralPath " + Q(part.FilePath) + ")"
                            : "(Get-Item -LiteralPath " + Q(part.FilePath) + ")";
                    if (part.ContentType is not null || (part.FileName is not null && part.FileName != Path.GetFileName(part.FilePath ?? "")))
                    {
                        warnings.Add($"Invoke-WebRequest -Form cannot set a content type or file name; they were left out for field '{part.Name}'.");
                    }

                    var index = fields.FindIndex(f => f.Name.Equals(part.Name, StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                    {
                        fields.Add((part.Name, [value]));
                    }
                    else
                    {
                        fields[index].Values.Add(value);
                    }
                }

                command.Add("-Form @{\n" + string.Join("\n  ", fields.Select(f =>
                    Q(f.Name) + "=" + (f.Values.Count == 1 ? f.Values[0] : "@(" + string.Join(", ", f.Values) + ")"))) + "\n}");
                needsPowerShell7.Add("-Form");
                return;
            default:
                if (body.Bytes is not null)
                {
                    if (CurlText.TryDecodeUtf8Text(body.Bytes, out var decoded))
                    {
                        AddTextBody(decoded, command);
                    }
                    else
                    {
                        command.Add("-Body ([System.Convert]::FromBase64String(" + Q(Convert.ToBase64String(body.Bytes)) + "))");
                    }
                }
                else if (body.Text is not null)
                {
                    AddTextBody(body.Text, command);
                }

                return;
        }
    }

    private static void AddTextBody(string text, List<string> command)
    {
        // As Chrome does: non-ASCII text goes as UTF-8 bytes, because Windows PowerShell would encode a string body as ISO-8859-1.
        command.Add(text.Any(c => c is < ' ' or > '~')
            ? "-Body ([System.Text.Encoding]::UTF8.GetBytes(" + Q(text) + "))"
            : "-Body " + Q(text));
    }

    private static void AddOptions(HttpRequestSpec request, RequestOptions o, List<string> command, List<string> warnings, List<string> needsPowerShell7)
    {
        command.Add("-MaximumRedirection " + (o.FollowRedirects ? Math.Max(o.MaxRedirects, 0) : 0).ToString(CultureInfo.InvariantCulture));
        if (o.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            command.Add("-TimeoutSec " + ((int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
        }

        if (o.ConnectTimeout is { } connectTimeout && connectTimeout > TimeSpan.Zero)
        {
            command.Add("-ConnectionTimeoutSeconds " + ((int)Math.Ceiling(connectTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
            needsPowerShell7.Add("-ConnectionTimeoutSeconds (7.4)");
        }

        if (o.Insecure)
        {
            command.Add("-SkipCertificateCheck");
            needsPowerShell7.Add("-SkipCertificateCheck");
        }

        if (!string.IsNullOrEmpty(o.Proxy))
        {
            command.Add("-Proxy " + Q(o.Proxy));
        }

        switch (request.HttpVersion)
        {
            case HttpVersionPreference.Http11:
                command.Add("-HttpVersion 1.1");
                needsPowerShell7.Add("-HttpVersion (7.3)");
                break;
            case HttpVersionPreference.Http2:
                command.Add("-HttpVersion 2.0");
                needsPowerShell7.Add("-HttpVersion (7.3)");
                break;
        }

        if (o.ClientCertificate is { } cert)
        {
            if (cert.Source == ClientCertificateSource.WindowsStore && !string.IsNullOrEmpty(cert.Thumbprint))
            {
                command.Add("-CertificateThumbprint " + Q(cert.Thumbprint));
            }
            else if (cert.Source == ClientCertificateSource.PfxFile && cert.Path is not null)
            {
                command.Add("-Certificate (Get-PfxCertificate -FilePath " + Q(cert.Path) + ")");
                if (!string.IsNullOrEmpty(cert.Password))
                {
                    warnings.Add("Get-PfxCertificate prompts for the certificate password; it is not written into the script.");
                }
            }
            else
            {
                warnings.Add("This client certificate cannot be expressed with Invoke-WebRequest and was left out.");
            }
        }

        if (o.ConnectOverrides.Count > 0)
        {
            warnings.Add("Invoke-WebRequest has no equivalent of --resolve or --connect-to; the connect overrides were left out.");
        }

        if (!o.AutoDecompress)
        {
            warnings.Add("Invoke-WebRequest always decompresses responses.");
        }
    }

    /// <summary>Splits a Cookie header into pairs that System.Net.Cookie accepts, or returns null when one does not fit.</summary>
    private static List<(string Name, string Value)>? ParseCookies(string header)
    {
        var cookies = new List<(string, string)>();
        foreach (var piece in header.Split(';'))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return null;
            }

            var name = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (name.StartsWith('$') || name.Any(c => c is ' ' or '\t' or ',' or ';' or '=') || value.Contains(',', StringComparison.Ordinal))
            {
                return null;
            }

            cookies.Add((name, value));
        }

        return cookies;
    }

    private static string DomainOf(string host)
    {
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']', StringComparison.Ordinal);
            return close < 0 ? host : host[..(close + 1)];
        }

        var colon = host.LastIndexOf(':');
        return colon < 0 ? host : host[..colon];
    }
}
