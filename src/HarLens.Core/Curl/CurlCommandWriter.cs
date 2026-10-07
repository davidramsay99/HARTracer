using System.Globalization;
using System.Text;
using HarLens.Core.Http;

namespace HarLens.Core.Curl;

/// <summary>
/// Writes a request as a curl command for bash, cmd.exe or PowerShell (SPEC 7.3). The output is designed so that
/// <see cref="CurlParser.Parse"/> reads back an equivalent request; every export is checked that way and any
/// difference is reported as a warning.
/// </summary>
internal static class CurlCommandWriter
{
    /// <summary>Placeholder file name for a body that is not text and cannot be written on a command line.</summary>
    public const string BinaryBodyPlaceholder = "request-body.bin";

    private const int CmdLineLimit = 8191;
    private const int CreateProcessLimit = 32767;

    /// <param name="Option">Option as written, or null for the positional URL.</param>
    /// <param name="Value">Argument, or null for a flag.</param>
    /// <param name="LongName">Long option name, used for <c>--expand-</c> when cmd.exe cannot carry the value.</param>
    /// <param name="Quote">False for simple numeric values that are written bare.</param>
    private sealed record Arg(string? Option, string? Value, string LongName, bool Quote = true);

    public static ExportResult Write(HttpRequestSpec request, CurlDialect dialect, ExportOptions options)
    {
        var warnings = new List<string>();
        var lossy = false;
        void Lossy(string message)
        {
            lossy = true;
            warnings.Add(message);
        }

        var args = new List<Arg>();
        var url = EscapeUrlGlob(request.Url);
        args.Add(url.StartsWith('-') ? new Arg("--url", url, "url") : new Arg(null, url, "url"));

        var body = request.Body;
        var parts = body.Mode == BodyMode.Multipart ? body.Parts.Where(p => p.Enabled).ToList() : [];
        var bodyKind = body.Mode switch
        {
            BodyMode.None => BodyKind.None,
            BodyMode.Multipart => parts.Count > 0 ? BodyKind.Multipart : BodyKind.None,
            BodyMode.BinaryFile => string.IsNullOrEmpty(body.FilePath) ? BodyKind.None : BodyKind.File,
            _ => body.Bytes is null && body.Text is null ? BodyKind.None : BodyKind.Text,
        };
        var hasBody = bodyKind != BodyKind.None;

        if (request.Method == "HEAD" && !hasBody)
        {
            args.Add(new Arg("-I", null, "head"));
        }
        else if (!(request.Method == "GET" && !hasBody) && !(request.Method == "POST" && hasBody))
        {
            args.Add(new Arg("-X", request.Method, "request"));
        }

        var headers = RequestExporter.ExportedHeaders(request, options).ToList();
        foreach (var header in headers)
        {
            // curl's -H forms: "Name: value" adds; "Name;" sends an empty value; a blank value cannot be sent.
            var blank = string.IsNullOrWhiteSpace(header.Value);
            if (header.Name.StartsWith(':'))
            {
                Lossy($"curl cannot send the HTTP/2 pseudo-header {header.Name}; it was exported but curl ignores it.");
            }
            else if (header.Name.Length == 0 || header.Name.Contains(':', StringComparison.Ordinal) || header.Name.StartsWith('@') ||
                (blank && header.Name.Contains(';', StringComparison.Ordinal)))
            {
                Lossy($"The header name '{header.Name}' cannot be written for curl's -H option.");
            }

            if (header.Value.Length > 0 && blank)
            {
                Lossy($"curl cannot send the blank value of header '{header.Name}'; it is exported as an empty value.");
            }

            args.Add(new Arg("-H", blank ? header.Name + ";" : header.Name + ": " + header.Value, "header"));
        }

        var hasContentType = headers.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        switch (bodyKind)
        {
            case BodyKind.Text:
                string? text = body.Text;
                if (body.Bytes is not null)
                {
                    text = CurlText.TryDecodeUtf8Text(body.Bytes, out var decoded) ? decoded : null;
                }

                if (text is null || text.Contains('\0', StringComparison.Ordinal))
                {
                    Lossy($"The body is binary and cannot be written on a command line; save it as {BinaryBodyPlaceholder} next to the command.");
                    AddSuppressContentType();
                    args.Add(new Arg("--data-binary", "@" + BinaryBodyPlaceholder, "data-binary"));
                }
                else
                {
                    AddSuppressContentType();
                    args.Add(new Arg("--data-raw", text, "data-raw"));
                }

                break;
            case BodyKind.File:
                if (body.FilePath == "-")
                {
                    Lossy("A body file named '-' would make curl read standard input.");
                }

                AddSuppressContentType();
                args.Add(new Arg("--data-binary", "@" + body.FilePath, "data-binary"));
                break;
            case BodyKind.Multipart:
                foreach (var part in parts)
                {
                    args.Add(FormArg(part, Lossy));
                }

                break;
        }

        void AddSuppressContentType()
        {
            if (!hasContentType)
            {
                // Without this, curl adds Content-Type: application/x-www-form-urlencoded.
                args.Add(new Arg("-H", "Content-Type:", "header"));
            }
        }

        AddOptionArgs(request, args, Lossy);

        var commandText = Render(args, dialect, warnings);
        var result = new ExportResult { Text = commandText };
        result.Warnings.AddRange(warnings);
        if (!lossy)
        {
            SelfCheck(request, options, dialect, result);
        }

        return result;
    }

    private enum BodyKind
    {
        None,
        Text,
        File,
        Multipart,
    }

    /// <summary>curl treats <c>[ ] { }</c> in a URL as glob syntax; Chrome escapes them with a backslash and so does HarLens.</summary>
    private static string EscapeUrlGlob(string url)
    {
        if (url.IndexOfAny(['[', ']', '{', '}']) < 0)
        {
            return url;
        }

        var sb = new StringBuilder(url.Length + 8);
        foreach (var c in url)
        {
            if (c is '[' or ']' or '{' or '}')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string FormQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static Arg FormArg(MultipartPart part, Action<string> lossy)
    {
        if (part.Name.Contains('=', StringComparison.Ordinal))
        {
            lossy($"The form field name '{part.Name}' contains '=', which curl's -F syntax cannot express.");
        }

        var sb = new StringBuilder(part.Name).Append('=');
        if (part.FilePath is not null)
        {
            sb.Append(part.FileContentAsValue ? '<' : '@').Append(FormQuote(part.FilePath));
            if (part.FileContentAsValue)
            {
                if (part.FileName is not null)
                {
                    lossy($"curl cannot attach a filename to the form field '{part.Name}', whose value is read from a file.");
                }
            }
            else if (part.FileName is not null && part.FileName != Path.GetFileName(part.FilePath))
            {
                sb.Append(";filename=").Append(FormQuote(part.FileName));
            }

            AppendType(sb, part.ContentType);
            return new Arg("-F", sb.ToString(), "form");
        }

        var value = part.Value ?? "";
        if (part.ContentType is null && part.FileName is null)
        {
            // --form-string takes the value literally, so no character in it is special.
            return new Arg("--form-string", part.Name + "=" + value, "form-string");
        }

        sb.Append(FormQuote(value));
        if (part.FileName is not null)
        {
            sb.Append(";filename=").Append(FormQuote(part.FileName));
        }

        AppendType(sb, part.ContentType);
        return new Arg("-F", sb.ToString(), "form");
    }

    private static void AppendType(StringBuilder sb, string? contentType)
    {
        if (contentType is not null)
        {
            sb.Append(";type=").Append(contentType);
        }
    }

    private static string Bracket(string host) => host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool IsPfxPath(string path) =>
        path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase);

    /// <summary>Escapes a certificate path for <c>-E</c>: <c>\</c> and <c>:</c> get a backslash, except a drive-letter colon.</summary>
    private static string EscapeCertPath(string path)
    {
        var sb = new StringBuilder(path.Length + 4);
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '\\')
            {
                sb.Append("\\\\");
            }
            else if (c == ':' && !(i == 1 && char.IsAsciiLetter(path[0]) && path.Length > 2 && path[2] is '\\' or '/'))
            {
                sb.Append("\\:");
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static void AddOptionArgs(HttpRequestSpec request, List<Arg> args, Action<string> lossy)
    {
        var o = request.Options;
        if (o.AutoDecompress)
        {
            args.Add(new Arg("--compressed", null, "compressed"));
        }

        if (o.FollowRedirects)
        {
            args.Add(new Arg("-L", null, "location"));
            if (o.MaxRedirects != 50)
            {
                args.Add(new Arg("--max-redirs", Number(o.MaxRedirects), "max-redirs", Quote: false));
            }
        }

        if (o.Insecure)
        {
            args.Add(new Arg("-k", null, "insecure"));
        }

        if (o.Timeout is { } timeout)
        {
            if (timeout > TimeSpan.Zero)
            {
                args.Add(new Arg("-m", CurlText.FormatSeconds(timeout), "max-time", Quote: false));
            }
            else
            {
                lossy("A timeout of zero or less cannot be given to curl, where -m 0 means no limit.");
            }
        }

        if (o.ConnectTimeout is { } connectTimeout)
        {
            if (connectTimeout > TimeSpan.Zero)
            {
                args.Add(new Arg("--connect-timeout", CurlText.FormatSeconds(connectTimeout), "connect-timeout", Quote: false));
            }
            else
            {
                lossy("A connect timeout of zero or less cannot be given to curl, where 0 means the default.");
            }
        }

        if (!string.IsNullOrEmpty(o.Proxy))
        {
            args.Add(new Arg("-x", o.Proxy, "proxy"));
        }

        foreach (var co in o.ConnectOverrides)
        {
            if (co.Kind == ConnectOverrideKind.Resolve)
            {
                if (co.Port == 0)
                {
                    lossy($"curl --resolve needs a port; the override for '{co.Host}' has none.");
                }

                var host = co.Host.Length == 0 ? "*" : Bracket(co.Host);
                args.Add(new Arg("--resolve", $"{host}:{Number(co.Port)}:{Bracket(co.TargetHost)}", "resolve"));
            }
            else
            {
                var port = co.Port == 0 ? "" : Number(co.Port);
                var targetPort = co.TargetPort == 0 ? "" : Number(co.TargetPort);
                args.Add(new Arg("--connect-to", $"{Bracket(co.Host)}:{port}:{Bracket(co.TargetHost)}:{targetPort}", "connect-to"));
            }
        }

        switch (request.HttpVersion)
        {
            case HttpVersionPreference.Http11:
                args.Add(new Arg("--http1.1", null, "http1.1"));
                break;
            case HttpVersionPreference.Http2:
                args.Add(new Arg("--http2", null, "http2"));
                break;
        }

        if (o.ClientCertificate is { } cert)
        {
            AddCertificateArgs(cert, args, lossy);
        }
    }

    private static void AddCertificateArgs(ClientCertificateSpec cert, List<Arg> args, Action<string> lossy)
    {
        if (cert.Source == ClientCertificateSource.WindowsStore)
        {
            if (string.IsNullOrEmpty(cert.Thumbprint))
            {
                lossy("The Windows certificate store entry has no thumbprint and was not exported.");
                return;
            }

            // curl on Windows (Schannel) reads "<store location>\<store name>\<thumbprint>".
            args.Add(new Arg("-E", EscapeCertPath($"{cert.StoreLocation}\\MY\\{cert.Thumbprint}"), "cert"));
            if (cert.Path is not null || !string.IsNullOrEmpty(cert.Password) || cert.KeyPath is not null)
            {
                lossy("Only the thumbprint of a Windows store certificate can be exported to curl.");
            }

            return;
        }

        var password = string.IsNullOrEmpty(cert.Password) ? null : cert.Password;
        if (cert.Path is not null)
        {
            if (cert.Path.StartsWith("pkcs11:", StringComparison.OrdinalIgnoreCase))
            {
                args.Add(new Arg("-E", cert.Path, "cert"));
                if (password is not null)
                {
                    args.Add(new Arg("--pass", password, "pass"));
                }
            }
            else
            {
                args.Add(new Arg("-E", EscapeCertPath(cert.Path) + (password is null ? "" : ":" + password), "cert"));
            }
        }
        else if (password is not null)
        {
            args.Add(new Arg("--pass", password, "pass"));
        }

        var inferred = cert.Path is not null && IsPfxPath(cert.Path) ? ClientCertificateSource.PfxFile : ClientCertificateSource.PemFile;
        if (cert.Source != inferred)
        {
            args.Add(new Arg("--cert-type", cert.Source == ClientCertificateSource.PfxFile ? "P12" : "PEM", "cert-type", Quote: false));
        }

        if (cert.KeyPath is not null)
        {
            args.Add(new Arg("--key", cert.KeyPath, "key"));
        }

        if (!string.IsNullOrEmpty(cert.Thumbprint))
        {
            lossy("A certificate thumbprint cannot be exported for a certificate file.");
        }
    }

    private static string Quote(string value, CurlDialect dialect) => dialect switch
    {
        CurlDialect.Cmd => ShellQuoting.Cmd(value),
        CurlDialect.PowerShell => ShellQuoting.PowerShell(value),
        _ => ShellQuoting.Bash(value),
    };

    private static string Render(List<Arg> args, CurlDialect dialect, List<string> warnings)
    {
        var parts = new List<string>(args.Count);
        var variableCount = 0;
        var needsLegacyQuoteWarning = false;
        foreach (var arg in args)
        {
            if (dialect == CurlDialect.Cmd && arg.Value is not null && arg.Value.Contains('\r', StringComparison.Ordinal))
            {
                // cmd.exe drops carriage returns from a command line. curl 8.12 and later can rebuild the value
                // from a base64 variable, which survives cmd.exe unchanged.
                var name = "harlens" + (++variableCount).ToString(CultureInfo.InvariantCulture);
                parts.Add("--variable " + Quote(name + "=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(arg.Value)), dialect));
                parts.Add("--expand-" + arg.LongName + " " + Quote("{{" + name + ":64dec}}", dialect));
                continue;
            }

            if (dialect == CurlDialect.PowerShell && arg.Value is not null && (arg.Value.Length == 0 || arg.Value.Any(PowerShellLexer.IsDoubleQuote)))
            {
                needsLegacyQuoteWarning = true;
            }

            var option = arg.Option;
            if (dialect == CurlDialect.PowerShell && option is not null && option.Contains('.', StringComparison.Ordinal))
            {
                // Windows PowerShell splits a bare argument such as --http1.1 at the dot.
                option = ShellQuoting.PowerShell(option);
            }

            // PowerShell parses bare numbers as numeric literals, so every PowerShell value is quoted.
            var value = arg.Value is null ? null : arg.Quote || dialect == CurlDialect.PowerShell ? Quote(arg.Value, dialect) : arg.Value;
            parts.Add(option is null ? value! : value is null ? option : option + " " + value);
        }

        if (variableCount > 0)
        {
            warnings.Add("cmd.exe cannot pass carriage returns on a command line, so values containing them are passed as base64 curl variables (--variable with {{name:64dec}}), which needs curl 8.12 or later.");
        }

        if (needsLegacyQuoteWarning)
        {
            warnings.Add("Windows PowerShell 5.1 and PowerShell 7.2 or earlier drop empty arguments and do not escape embedded double quotes when calling curl.exe; run this command in PowerShell 7.3 or later.");
        }

        var program = dialect == CurlDialect.PowerShell ? "curl.exe" : "curl";
        var separator = parts.Count >= 3
            ? dialect switch
            {
                CurlDialect.Cmd => " ^\n  ",
                CurlDialect.PowerShell => " `\n  ",
                _ => " \\\n  ",
            }
            : " ";
        var text = program + " " + string.Join(separator, parts);

        if (dialect == CurlDialect.Cmd && text.Length > CmdLineLimit)
        {
            warnings.Add($"The command is {text.Length} characters long; cmd.exe accepts at most {CmdLineLimit}.");
        }
        else if (dialect != CurlDialect.Bash && text.Length > CreateProcessLimit)
        {
            warnings.Add($"The command is {text.Length} characters long; Windows accepts at most {CreateProcessLimit} for a process command line.");
        }

        return text;
    }

    /// <summary>Parses the generated command and reports any way in which it differs from the request.</summary>
    private static void SelfCheck(HttpRequestSpec request, ExportOptions options, CurlDialect dialect, ExportResult result)
    {
        var expected = request.Clone();
        expected.Headers = RequestExporter.ExportedHeaders(request, options).Select(h => h.Clone()).ToList();
        var parsed = CurlParser.Parse(result.Text, new CurlParseOptions { Dialect = dialect });
        var differences = RequestEquivalence.Differences(parsed.Request, expected);
        if (differences.Count > 0)
        {
            result.Warnings.Add("The exported command does not reproduce the request exactly: " + string.Join("; ", differences.Take(5)));
        }
    }
}
