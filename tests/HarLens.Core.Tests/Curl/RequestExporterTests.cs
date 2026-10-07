using HarLens.Core.Curl;
using HarLens.Core.Http;

namespace HarLens.Core.Tests.Curl;

public class RequestExporterTests
{
    /// <summary>A captured-looking POST with the things every format has to handle.</summary>
    internal static HttpRequestSpec Representative() => new()
    {
        Method = "POST",
        Url = "https://api.example.com/v1/items?id=7&tag[]=a",
        Headers =
        [
            new HeaderEntry(":authority", "api.example.com"),
            new HeaderEntry("accept", "application/json"),
            new HeaderEntry("content-type", "application/json"),
            new HeaderEntry("content-length", "40"),
            new HeaderEntry("cookie", "sid=abc; theme=dark"),
            new HeaderEntry("user-agent", "HarLens/1.0"),
            new HeaderEntry("x-note", "it's 100% \"ok\""),
            new HeaderEntry("x-off", "disabled", enabled: false),
        ],
        Body = RequestBody.FromText("{\"name\":\"Zoë\",\"path\":\"C:\\\\tmp\",\"n\":1}\n", BodyMode.Json),
        Options = new RequestOptions { AutoDecompress = true, FollowRedirects = true, Timeout = TimeSpan.FromSeconds(30) },
    };

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- snapshots ----

    [Fact]
    public void SnapshotCurlBash()
    {
        var result = RequestExporter.Export(Representative(), ExportFormat.CurlBash);
        Assert.Equal(Lf("""
            curl 'https://api.example.com/v1/items?id=7&tag\[\]=a' \
              -H 'accept: application/json' \
              -H 'content-type: application/json' \
              -H 'cookie: sid=abc; theme=dark' \
              -H 'user-agent: HarLens/1.0' \
              -H $'x-note: it\'s 100% "ok"' \
              --data-raw $'{"name":"Zoë","path":"C:\\\\tmp","n":1}\n' \
              --compressed \
              -L \
              -m 30
            """), result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SnapshotCurlCmd()
    {
        var result = RequestExporter.Export(Representative(), ExportFormat.CurlCmd);
        Assert.Equal(Lf("""
            curl ^"https://api.example.com/v1/items?id=7^&tag^\^[^\^]=a^" ^
              -H ^"accept: application/json^" ^
              -H ^"content-type: application/json^" ^
              -H ^"cookie: sid=abc; theme=dark^" ^
              -H ^"user-agent: HarLens/1.0^" ^
              -H ^"x-note: it's 100^% ^\^"ok^\^"^" ^
              --data-raw ^"^{^\^"name^\^":^\^"Zo^ë^\^",^\^"path^\^":^\^"C:^\^\tmp^\^",^\^"n^\^":1^}^

            ^" ^
              --compressed ^
              -L ^
              -m 30
            """), result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SnapshotCurlPowerShell()
    {
        var result = RequestExporter.Export(Representative(), ExportFormat.CurlPowerShell);
        Assert.Equal(Lf("""
            curl.exe 'https://api.example.com/v1/items?id=7&tag\[\]=a' `
              -H 'accept: application/json' `
              -H 'content-type: application/json' `
              -H 'cookie: sid=abc; theme=dark' `
              -H 'user-agent: HarLens/1.0' `
              -H 'x-note: it''s 100% "ok"' `
              --data-raw "{`"name`":`"Zoë`",`"path`":`"C:\\tmp`",`"n`":1}`n" `
              --compressed `
              -L `
              -m '30'
            """), result.Text);
        Assert.Contains("PowerShell 7.3", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPowerShellInvokeWebRequest()
    {
        var result = RequestExporter.Export(Representative(), ExportFormat.PowerShellInvokeWebRequest);
        Assert.Equal(Lf("""
            $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
            $session.UserAgent = "HarLens/1.0"
            $session.Cookies.Add((New-Object System.Net.Cookie("sid", "abc", "/", "api.example.com")))
            $session.Cookies.Add((New-Object System.Net.Cookie("theme", "dark", "/", "api.example.com")))
            Invoke-WebRequest -UseBasicParsing -Uri "https://api.example.com/v1/items?id=7&tag[]=a" `
            -Method "POST" `
            -WebSession $session `
            -Headers @{
            "accept"="application/json"
              "x-note"="it's 100% `"ok`""
            } `
            -ContentType "application/json" `
            -Body ([System.Text.Encoding]::UTF8.GetBytes("{`"name`":`"Zo$([char]235)`",`"path`":`"C:\\tmp`",`"n`":1}$([char]10)")) `
            -MaximumRedirection 50 `
            -TimeoutSec 30
            """), result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SnapshotRawHttp()
    {
        var result = RequestExporter.Export(Representative(), ExportFormat.RawHttp);
        Assert.Equal(
            "POST /v1/items?id=7&tag[]=a HTTP/1.1\r\n" +
            "Host: api.example.com\r\n" +
            "accept: application/json\r\n" +
            "content-type: application/json\r\n" +
            "cookie: sid=abc; theme=dark\r\n" +
            "user-agent: HarLens/1.0\r\n" +
            "x-note: it's 100% \"ok\"\r\n" +
            "\r\n" +
            "{\"name\":\"Zoë\",\"path\":\"C:\\\\tmp\",\"n\":1}\n",
            result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void IncludeOptionsAddPseudoHeadersAndContentLength()
    {
        var text = RequestExporter.Export(Representative(), ExportFormat.RawHttp, new ExportOptions { IncludeContentLength = true, IncludePseudoHeaders = true }).Text;
        Assert.Contains("\r\n:authority: api.example.com\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\r\ncontent-length: 40\r\n", text, StringComparison.Ordinal);
    }

    // ---- curl specifics ----

    [Theory]
    [InlineData("GET", false, "")]
    [InlineData("GET", true, "-X 'GET'")]
    [InlineData("POST", true, "")]
    [InlineData("POST", false, "-X 'POST'")]
    [InlineData("HEAD", false, "-I")]
    [InlineData("HEAD", true, "-X 'HEAD'")]
    [InlineData("get", false, "-X 'get'")]
    public void MethodIsWrittenOnlyWhenNotImplied(string method, bool withBody, string expected)
    {
        var request = new HttpRequestSpec { Method = method, Url = "https://x/", Body = withBody ? RequestBody.FromText("b") : RequestBody.None() };
        var text = RequestExporter.Export(request, ExportFormat.CurlBash).Text;
        if (expected.Length == 0)
        {
            Assert.DoesNotContain("-X", text, StringComparison.Ordinal);
            Assert.DoesNotContain("-I", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TextBodyWithoutContentTypeSuppressesCurlsDefault()
    {
        var text = RequestExporter.Export(new HttpRequestSpec { Method = "POST", Url = "https://x/", Body = RequestBody.FromText("a"), Options = { AutoDecompress = false } }, ExportFormat.CurlBash).Text;
        Assert.Equal("curl 'https://x/' \\\n  -H 'Content-Type:' \\\n  --data-raw 'a'", text);
    }

    [Fact]
    public void ShortCommandsStayOnOneLine()
    {
        Assert.Equal("curl 'https://x/'", RequestExporter.Export(new HttpRequestSpec { Url = "https://x/", Options = { AutoDecompress = false } }, ExportFormat.CurlBash).Text);
        Assert.Equal("curl ^\"https://x/^\" --compressed", RequestExporter.Export(new HttpRequestSpec { Url = "https://x/", Options = { AutoDecompress = true } }, ExportFormat.CurlCmd).Text);
        Assert.Equal("curl.exe 'https://x/' -I", RequestExporter.Export(new HttpRequestSpec { Method = "HEAD", Url = "https://x/", Options = { AutoDecompress = false } }, ExportFormat.CurlPowerShell).Text);
    }

    [Fact]
    public void MultipartArguments()
    {
        var request = new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://x/",
            Body = new RequestBody
            {
                Mode = BodyMode.Multipart,
                Parts =
                [
                    new MultipartPart { Name = "plain", Value = "@not;a \"file\"" },
                    new MultipartPart { Name = "typed", Value = "{\"a\":1}", ContentType = "application/json" },
                    new MultipartPart { Name = "up", FilePath = "C:\\in\\a,b.png", ContentType = "image/png", FileName = "x.png" },
                    new MultipartPart { Name = "same", FilePath = "dir/same.txt", FileName = "same.txt" },
                    new MultipartPart { Name = "val", FilePath = "v.txt", FileContentAsValue = true },
                    new MultipartPart { Name = "off", Value = "x", Enabled = false },
                ],
            },
            Options = { AutoDecompress = false },
        };

        var text = RequestExporter.Export(request, ExportFormat.CurlBash).Text;
        Assert.Equal(
            "curl 'https://x/' \\\n" +
            "  --form-string 'plain=@not;a \"file\"' \\\n" +
            "  -F 'typed=\"{\\\"a\\\":1}\";type=application/json' \\\n" +
            "  -F 'up=@\"C:\\\\in\\\\a,b.png\";filename=\"x.png\";type=image/png' \\\n" +
            "  -F 'same=@\"dir/same.txt\"' \\\n" +
            "  -F 'val=<\"v.txt\"'",
            text);
    }

    [Fact]
    public void ConnectionOptions()
    {
        var request = new HttpRequestSpec
        {
            Url = "https://x/",
            HttpVersion = HttpVersionPreference.Http11,
            Options = new RequestOptions
            {
                AutoDecompress = false,
                FollowRedirects = true,
                MaxRedirects = 3,
                Insecure = true,
                Timeout = TimeSpan.FromMilliseconds(1500),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                Proxy = "http://p:3128",
                ConnectOverrides =
                [
                    new ConnectOverride { Kind = ConnectOverrideKind.Resolve, Host = "", Port = 443, TargetHost = "::1" },
                    new ConnectOverride { Kind = ConnectOverrideKind.ConnectTo, Host = "x", Port = 0, TargetHost = "10.0.0.1", TargetPort = 8443 },
                ],
                ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.PfxFile, Path = "C:\\c\\a:b.pfx", Password = "p:w", KeyPath = "k.pem" },
            },
        };

        var text = RequestExporter.Export(request, ExportFormat.CurlBash).Text;
        Assert.Equal(
            "curl 'https://x/' \\\n" +
            "  -L \\\n  --max-redirs 3 \\\n  -k \\\n  -m 1.5 \\\n  --connect-timeout 2 \\\n  -x 'http://p:3128' \\\n" +
            "  --resolve '*:443:[::1]' \\\n  --connect-to 'x::10.0.0.1:8443' \\\n  --http1.1 \\\n" +
            "  -E 'C:\\\\c\\\\a\\:b.pfx:p:w' \\\n  --key 'k.pem'",
            text);
    }

    [Fact]
    public void WindowsStoreCertificateUsesSchannelSyntax()
    {
        var request = new HttpRequestSpec
        {
            Url = "https://x/",
            Options = { AutoDecompress = false, ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.WindowsStore, StoreLocation = "CurrentUser", Thumbprint = "ABCDEF" } },
        };

        Assert.Equal("curl 'https://x/' -E 'CurrentUser\\\\MY\\\\ABCDEF'", RequestExporter.Export(request, ExportFormat.CurlBash).Text);
    }

    [Fact]
    public void BinaryFileBody()
    {
        var request = new HttpRequestSpec { Method = "PUT", Url = "https://x/", Body = new RequestBody { Mode = BodyMode.BinaryFile, FilePath = "a b.bin" }, Options = { AutoDecompress = false } };
        Assert.Equal("curl 'https://x/' \\\n  -X 'PUT' \\\n  -H 'Content-Type:' \\\n  --data-binary '@a b.bin'", RequestExporter.Export(request, ExportFormat.CurlBash).Text);
    }

    [Fact]
    public void CmdPassesCarriageReturnsThroughABase64Variable()
    {
        var request = new HttpRequestSpec { Method = "POST", Url = "https://x/", Headers = [new HeaderEntry("Content-Type", "text/plain")], Body = RequestBody.FromText("a\r\nb") };
        var result = RequestExporter.Export(request, ExportFormat.CurlCmd);
        Assert.Contains("--variable ^\"harlens1=YQ0KYg==^\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("--expand-data-raw ^\"^{^{harlens1:64dec^}^}^\"", result.Text, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("8.12", StringComparison.Ordinal));
        Assert.Equal("a\r\nb", CurlParser.Parse(result.Text).Request.Body.Text);
    }

    [Fact]
    public void CmdWarnsWhenTheCommandIsTooLongForCmdExe()
    {
        var request = new HttpRequestSpec { Method = "POST", Url = "https://x/", Headers = [new HeaderEntry("Content-Type", "text/plain")], Body = RequestBody.FromText(new string('a', 9000)) };
        Assert.Contains(RequestExporter.Export(request, ExportFormat.CurlCmd).Warnings, w => w.Contains("8191", StringComparison.Ordinal));
    }

    [Fact]
    public void UrlStartingWithDashUsesUrlOption()
    {
        Assert.Equal("curl --url '-x'", RequestExporter.Export(new HttpRequestSpec { Url = "-x", Options = { AutoDecompress = false } }, ExportFormat.CurlBash).Text);
    }

    [Fact]
    public void PowerShellQuotesOptionsWithADot()
    {
        var text = RequestExporter.Export(new HttpRequestSpec { Url = "https://x/", HttpVersion = HttpVersionPreference.Http11, Options = { AutoDecompress = false } }, ExportFormat.CurlPowerShell).Text;
        Assert.Equal("curl.exe 'https://x/' '--http1.1'", text);
        Assert.Equal(HttpVersionPreference.Http11, CurlParser.Parse(text).Request.HttpVersion);
    }

    // ---- Invoke-WebRequest specifics ----

    [Fact]
    public void InvokeWebRequestFallsBackToHeadersForCookiesSystemNetCookieRejects()
    {
        var request = new HttpRequestSpec { Url = "https://x/", Headers = [new HeaderEntry("Cookie", "a=1,2; b=3")], Options = { AutoDecompress = true } };
        var text = RequestExporter.Export(request, ExportFormat.PowerShellInvokeWebRequest).Text;
        Assert.DoesNotContain("$session", text, StringComparison.Ordinal);
        Assert.Contains("\"Cookie\"=\"a=1,2; b=3\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeWebRequestMergesDuplicateHeadersAndReportsDroppedOnes()
    {
        var request = new HttpRequestSpec
        {
            Url = "https://x/",
            Headers = [new HeaderEntry("X-A", "1"), new HeaderEntry("x-a", "2"), new HeaderEntry("Host", "h"), new HeaderEntry("Connection", "close")],
            Options = { AutoDecompress = true },
        };
        var result = RequestExporter.Export(request, ExportFormat.PowerShellInvokeWebRequest);
        Assert.Contains("\"X-A\"=\"1, 2\"", result.Text, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("Host, Connection", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("X-A", StringComparison.Ordinal));
    }

    [Fact]
    public void InvokeWebRequestBodies()
    {
        string Export(RequestBody body) => RequestExporter.Export(new HttpRequestSpec { Method = "POST", Url = "https://x/", Body = body, Options = { AutoDecompress = true } }, ExportFormat.PowerShellInvokeWebRequest).Text;

        Assert.Contains("-Body \"a=1&b=`$2\"", Export(RequestBody.FromText("a=1&b=$2")), StringComparison.Ordinal);
        Assert.Contains("-InFile \"C:\\data\\x.bin\"", Export(new RequestBody { Mode = BodyMode.BinaryFile, FilePath = "C:\\data\\x.bin" }), StringComparison.Ordinal);
        Assert.Contains("-Body ([System.Convert]::FromBase64String(\"AP8=\"))", Export(new RequestBody { Mode = BodyMode.Raw, Bytes = [0x00, 0xFF] }), StringComparison.Ordinal);
        var form = Export(new RequestBody
        {
            Mode = BodyMode.Multipart,
            Parts = [new MultipartPart { Name = "a", Value = "1" }, new MultipartPart { Name = "a", Value = "2" }, new MultipartPart { Name = "f", FilePath = "p.png" }],
        });
        Assert.Contains("-Form @{\n\"a\"=@(\"1\", \"2\")\n  \"f\"=(Get-Item -LiteralPath \"p.png\")\n}", form, StringComparison.Ordinal);
    }

    [Fact]
    public void InvokeWebRequestOptions()
    {
        var request = new HttpRequestSpec
        {
            Url = "https://x/",
            HttpVersion = HttpVersionPreference.Http2,
            Options = new RequestOptions
            {
                Insecure = true,
                Proxy = "http://p:1",
                ConnectTimeout = TimeSpan.FromSeconds(1.2),
                ConnectOverrides = [new ConnectOverride { Host = "x", Port = 443, TargetHost = "1.2.3.4" }],
                ClientCertificate = new ClientCertificateSpec { Source = ClientCertificateSource.WindowsStore, Thumbprint = "AB" },
            },
        };
        var result = RequestExporter.Export(request, ExportFormat.PowerShellInvokeWebRequest);
        Assert.Contains("-MaximumRedirection 0", result.Text, StringComparison.Ordinal);
        Assert.Contains("-ConnectionTimeoutSeconds 2", result.Text, StringComparison.Ordinal);
        Assert.Contains("-SkipCertificateCheck", result.Text, StringComparison.Ordinal);
        Assert.Contains("-Proxy \"http://p:1\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("-HttpVersion 2.0", result.Text, StringComparison.Ordinal);
        Assert.Contains("-CertificateThumbprint \"AB\"", result.Text, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("--resolve", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("PowerShell 7", StringComparison.Ordinal));
    }

    // ---- raw HTTP specifics ----

    [Fact]
    public void RawHttpMultipartUsesTheContentTypeBoundaryOrAddsOne()
    {
        var body = new RequestBody
        {
            Mode = BodyMode.Multipart,
            Parts =
            [
                new MultipartPart { Name = "a\"b", Value = "v" },
                new MultipartPart { Name = "f", FilePath = "dir/p.png", ContentType = "image/png" },
                new MultipartPart { Name = "c", FilePath = "c.csv", FileContentAsValue = true },
            ],
        };

        var withoutHeader = RequestExporter.Export(new HttpRequestSpec { Method = "POST", Url = "http://h:8080", Body = body }, ExportFormat.RawHttp).Text;
        Assert.Equal(
            "POST / HTTP/1.1\r\nHost: h:8080\r\nContent-Type: multipart/form-data; boundary=----HarLensFormBoundary7MA4YWxkTrZu0gW\r\n\r\n" +
            "------HarLensFormBoundary7MA4YWxkTrZu0gW\r\nContent-Disposition: form-data; name=\"a%22b\"\r\n\r\nv\r\n" +
            "------HarLensFormBoundary7MA4YWxkTrZu0gW\r\nContent-Disposition: form-data; name=\"f\"; filename=\"p.png\"\r\nContent-Type: image/png\r\n\r\n<contents of dir/p.png>\r\n" +
            "------HarLensFormBoundary7MA4YWxkTrZu0gW\r\nContent-Disposition: form-data; name=\"c\"\r\n\r\n<contents of c.csv>\r\n" +
            "------HarLensFormBoundary7MA4YWxkTrZu0gW--\r\n",
            withoutHeader);

        var withHeader = RequestExporter.Export(new HttpRequestSpec
        {
            Method = "POST",
            Url = "http://h/u?q",
            Headers = [new HeaderEntry("Content-Type", "multipart/form-data; boundary=\"XyZ\"")],
            Body = body,
        }, ExportFormat.RawHttp).Text;
        Assert.StartsWith("POST /u?q HTTP/1.1\r\nHost: h\r\nContent-Type: multipart/form-data; boundary=\"XyZ\"\r\n\r\n--XyZ\r\n", withHeader, StringComparison.Ordinal);
        Assert.EndsWith("--XyZ--\r\n", withHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void RawHttpKeepsAnExplicitHostAndShowsPlaceholdersForFiles()
    {
        var request = new HttpRequestSpec
        {
            Method = "PUT",
            Url = "https://user:pw@h.example/a#frag",
            Headers = [new HeaderEntry("X", "1"), new HeaderEntry("Host", "other")],
            Body = new RequestBody { Mode = BodyMode.BinaryFile, FilePath = "blob.bin" },
        };
        Assert.Equal("PUT /a HTTP/1.1\r\nX: 1\r\nHost: other\r\n\r\n<contents of blob.bin>", RequestExporter.Export(request, ExportFormat.RawHttp).Text);

        var binary = RequestExporter.Export(new HttpRequestSpec { Method = "POST", Url = "https://user:pw@h.example?x=1", Body = new RequestBody { Mode = BodyMode.Raw, Bytes = [0xFF, 0x00] } }, ExportFormat.RawHttp);
        Assert.Equal("POST /?x=1 HTTP/1.1\r\nHost: h.example\r\n\r\n<2 bytes of binary data>", binary.Text);
        Assert.Single(binary.Warnings);
    }

    [Fact]
    public void UnknownFormatThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestExporter.Export(new HttpRequestSpec(), (ExportFormat)99));
    }
}
