using HarLens.Core.Http;

namespace HarLens.Core.Tests.Curl;

/// <summary>
/// Requests built to break shell quoting: every kind of quote, backslashes before quotes, cmd.exe and PowerShell
/// metacharacters, variables, control characters, line feeds and CRLF, non-ASCII text and very long values.
/// Excluded on purpose, because curl itself cannot express them (see DECISIONS.md): NUL characters, header values
/// made only of blanks, header names that are not HTTP tokens, form field names containing '=', and non-text bodies.
/// </summary>
internal static class AdversarialCorpus
{
    public static readonly string[] NastyStrings =
    [
        "plain",
        "",
        " leading space",
        "trailing space ",
        "   both   ",
        "\tleading tab",
        "it's",
        "''",
        "'",
        "\"",
        "\"double\" quotes",
        "say \"hi\"",
        "\\",
        "back\\slash",
        "trailing backslash\\",
        "two trailing\\\\",
        "\\\"",
        "\\\\\"",
        "quote at end\"",
        "\\\\server\\share\\",
        "C:\\Program Files\\App\\",
        "%PATH%",
        "%%",
        "100%",
        "%^",
        "50%25 off",
        "%USERPROFILE%\\x",
        "!bang!",
        "!!",
        "^caret^",
        "^^",
        "^\"",
        "&|<>()",
        "a & b | c > d < e",
        "a&&b||c",
        "`backtick`",
        "``",
        "$var",
        "${var}",
        "$(whoami)",
        "$env:PATH",
        "@(1,2)",
        "{json: \"x\"}",
        "[glob]",
        "{a,b}",
        "a;b;c",
        "a=b=c",
        "@at",
        "<lt",
        "-dash",
        "--double-dash",
        "#hash",
        "~tilde",
        "line1\nline2",
        "crlf\r\nline",
        "cr only\r",
        "\n",
        "\r\n",
        "ends with newline\n",
        "tab\there",
        "ctrl\u0001\u0007\u001b\u001f\u007f",
        "bell\a vtab\v formfeed\f backspace\b",
        "café naïve",
        "東京",
        "😀👍🏽",
        "\u2018smart\u2019 \u201csmart\u201d \u201a\u201b\u201e",
        "nbsp\u00a0and\u2028separator",
        "\u0085next line",
        "mixed '\"\\`$%!^&|<>\r\n\t end\\",
        "--% stop parsing",
        "& 'call'",
        new string('x', 3000) + "\"'\\%end",
    ];

    /// <summary>Strings usable as header values (not made only of blanks).</summary>
    public static IEnumerable<string> HeaderValues => NastyStrings.Where(s => s.Length == 0 || !string.IsNullOrWhiteSpace(s));

    public static IEnumerable<(string Name, HttpRequestSpec Request)> Requests()
    {
        var i = 0;
        foreach (var s in NastyStrings)
        {
            i++;
            yield return ($"text body {i}", new HttpRequestSpec
            {
                Method = "POST",
                Url = "https://example.com/post",
                Headers = [new HeaderEntry("Content-Type", "text/plain")],
                Body = RequestBody.FromText(s),
                Options = { AutoDecompress = false },
            });
            yield return ($"body without content type {i}", new HttpRequestSpec
            {
                Method = "PUT",
                Url = "https://example.com/put",
                Body = RequestBody.FromText(s),
            });
            yield return ($"url {i}", new HttpRequestSpec
            {
                Url = "https://example.com/p?q=" + s + "#frag",
            });
            yield return ($"method {i}", new HttpRequestSpec
            {
                Method = s.Length == 0 ? "GET" : s,
                Url = "https://example.com/m",
            });
            yield return ($"multipart {i}", new HttpRequestSpec
            {
                Method = "POST",
                Url = "https://example.com/upload",
                Body = new RequestBody
                {
                    Mode = BodyMode.Multipart,
                    Parts =
                    [
                        new MultipartPart { Name = s.Replace("=", "", StringComparison.Ordinal), Value = s },
                        new MultipartPart { Name = "typed", Value = s, ContentType = "text/plain; charset=utf-8" },
                        new MultipartPart { Name = "named", Value = s, FileName = s },
                        new MultipartPart { Name = "file", FilePath = s.Length == 0 ? "x" : s, ContentType = "application/octet-stream", FileName = "f-" + s },
                        new MultipartPart { Name = "file2", FilePath = "dir/" + s + ".bin" },
                        new MultipartPart { Name = "asvalue", FilePath = "v" + s, FileContentAsValue = true, ContentType = "text/csv" },
                        new MultipartPart { Name = "disabled", Value = "never", Enabled = false },
                    ],
                },
            });
            yield return ($"binary file {i}", new HttpRequestSpec
            {
                Method = "POST",
                Url = "https://example.com/bin",
                Headers = [new HeaderEntry("Content-Type", "application/octet-stream")],
                Body = new RequestBody { Mode = BodyMode.BinaryFile, FilePath = "data/" + s },
            });
            yield return ($"cert {i}", new HttpRequestSpec
            {
                Url = "https://example.com/cert",
                Options =
                {
                    ClientCertificate = new ClientCertificateSpec
                    {
                        Source = ClientCertificateSource.PfxFile,
                        Path = "C:\\certs\\" + s + ":x.pfx",
                        Password = s,
                        KeyPath = "keys/" + s,
                    },
                },
            });
            yield return ($"proxy {i}", new HttpRequestSpec
            {
                Url = "https://example.com/proxy",
                Options = { Proxy = "http://user:" + s + "@proxy.local:8080" },
            });
        }

        var headers = new HttpRequestSpec { Url = "https://example.com/headers", Method = "GET" };
        var n = 0;
        foreach (var s in HeaderValues)
        {
            headers.Headers.Add(new HeaderEntry("X-Test-" + (++n), s));
        }

        headers.Headers.Add(new HeaderEntry("X-Test-1", "duplicate name"));
        headers.Headers.Add(new HeaderEntry("Cookie", "a=1; b=\"two\"; c=%PATH%"));
        headers.Headers.Add(new HeaderEntry("User-Agent", "agent 'quoted'"));
        headers.Headers.Add(new HeaderEntry("Authorization", "Basic dXNlcjpwYXNz"));
        headers.Headers.Add(new HeaderEntry("X-Disabled", "not exported", enabled: false));
        yield return ("all header values", headers);

        foreach (var method in new[] { "GET", "POST", "HEAD", "DELETE", "patch", "M-SEARCH", "OPTIONS" })
        {
            foreach (var withBody in new[] { false, true })
            {
                yield return ($"method {method} body {withBody}", new HttpRequestSpec
                {
                    Method = method,
                    Url = "https://example.com/" + method,
                    Body = withBody ? RequestBody.FromText("x=1", BodyMode.FormUrlEncoded) : RequestBody.None(),
                    Headers = withBody ? [new HeaderEntry("Content-Type", "application/x-www-form-urlencoded")] : [],
                });
            }
        }

        yield return ("empty text body", new HttpRequestSpec { Method = "POST", Url = "https://example.com/e", Body = RequestBody.FromText("") });
        yield return ("json body", new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://example.com/j",
            Headers = [new HeaderEntry("Content-Type", "application/json; charset=utf-8"), new HeaderEntry("Accept", "*/*")],
            Body = RequestBody.FromText("{\"a\":\"b\\\"c\",\"d\":[1,2],\"e\":\"%PATH%\"}", BodyMode.Json),
        });
        yield return ("empty content type with body", new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://example.com/ct",
            Headers = [new HeaderEntry("Content-Type", "")],
            Body = RequestBody.FromText("abc"),
        });
        yield return ("utf8 bytes body", new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://example.com/bytes",
            Headers = [new HeaderEntry("Content-Type", "text/plain")],
            Body = new RequestBody { Mode = BodyMode.Raw, Bytes = "héllo\r\n"u8.ToArray() },
        });
        yield return ("ipv6 url with brackets", new HttpRequestSpec { Url = "http://[::1]:8080/a[1]{2}" });
        yield return ("very long url", new HttpRequestSpec { Url = "https://example.com/?" + string.Concat(Enumerable.Repeat("k=v%20&", 400)) });

        yield return ("every option", new HttpRequestSpec
        {
            Method = "GET",
            Url = "https://api.example.com/opts",
            HttpVersion = HttpVersionPreference.Http2,
            Options = new RequestOptions
            {
                FollowRedirects = true,
                MaxRedirects = 7,
                Timeout = TimeSpan.FromTicks(15_000_001),
                ConnectTimeout = TimeSpan.FromMilliseconds(250),
                AutoDecompress = true,
                Insecure = true,
                Proxy = "socks5h://proxy.local:1080",
                ConnectOverrides =
                [
                    new ConnectOverride { Kind = ConnectOverrideKind.Resolve, Host = "api.example.com", Port = 443, TargetHost = "127.0.0.1" },
                    new ConnectOverride { Kind = ConnectOverrideKind.Resolve, Host = "", Port = 80, TargetHost = "::1" },
                    new ConnectOverride { Kind = ConnectOverrideKind.ConnectTo, Host = "", Port = 0, TargetHost = "fe80::1", TargetPort = 8443 },
                    new ConnectOverride { Kind = ConnectOverrideKind.ConnectTo, Host = "::1", Port = 443, TargetHost = "", TargetPort = 0 },
                    new ConnectOverride { Kind = ConnectOverrideKind.ConnectTo, Host = "a.example", Port = 0, TargetHost = "b.example", TargetPort = 0 },
                ],
                ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.PemFile, Path = "/etc/ssl/client.pem", KeyPath = "/etc/ssl/client.key", Password = "pa:ss\\word" },
            },
        });
        yield return ("http 1.1, follow with default max", new HttpRequestSpec
        {
            Url = "https://example.com/h11",
            HttpVersion = HttpVersionPreference.Http11,
            Options = { FollowRedirects = true, MaxRedirects = 50, AutoDecompress = false },
        });
        yield return ("unlimited redirects", new HttpRequestSpec { Url = "https://example.com/r", Options = { FollowRedirects = true, MaxRedirects = -1 } });
        yield return ("pem file named .pfx", new HttpRequestSpec
        {
            Url = "https://example.com/c",
            Options = { ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.PemFile, Path = "odd.pfx" } },
        });
        yield return ("pfx file without extension", new HttpRequestSpec
        {
            Url = "https://example.com/c",
            Options = { ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.PfxFile, Path = "C:/certs/client", Password = "pw" } },
        });
        yield return ("drive relative cert path", new HttpRequestSpec
        {
            Url = "https://example.com/c",
            Options = { ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.PemFile, Path = "C:relative.pem" } },
        });
        yield return ("unc cert path", new HttpRequestSpec
        {
            Url = "https://example.com/c",
            Options = { ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.PfxFile, Path = "\\\\server\\share\\c.p12", Password = "x" } },
        });
        yield return ("windows store cert", new HttpRequestSpec
        {
            Url = "https://example.com/c",
            Options = { ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.WindowsStore, StoreLocation = "LocalMachine", Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567" } },
        });
    }
}
