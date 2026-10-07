using HarLens.Core.Curl;
using HarLens.Core.Http;

namespace HarLens.Core.Tests.Curl;

public class CurlParserTests
{
    private static CurlImportResult Parse(string command) => CurlParser.Parse(command);

    private static HttpRequestSpec Request(string command) => CurlParser.Parse(command).Request;

    private static List<(string Name, string Value)> Headers(HttpRequestSpec request) =>
        request.Headers.Select(h => (h.Name, h.Value)).ToList();

    // ---- method ----

    [Theory]
    [InlineData("curl https://x/", "GET")]
    [InlineData("curl -d a https://x/", "POST")]
    [InlineData("curl --data-raw '' https://x/", "POST")]
    [InlineData("curl -F a=b https://x/", "POST")]
    [InlineData("curl --json '{}' https://x/", "POST")]
    [InlineData("curl -I https://x/", "HEAD")]
    [InlineData("curl --head https://x/", "HEAD")]
    [InlineData("curl -G -d a=1 https://x/", "GET")]
    [InlineData("curl -G -I -d a=1 https://x/", "HEAD")]
    [InlineData("curl -X DELETE https://x/", "DELETE")]
    [InlineData("curl -XPUT -d a https://x/", "PUT")]
    [InlineData("curl --request patch https://x/", "patch")]
    [InlineData("curl -X GET -d a https://x/", "GET")]
    public void MethodFollowsCurl(string command, string method)
    {
        Assert.Equal(method, Request(command).Method);
    }

    // ---- URL ----

    [Fact]
    public void UrlWithoutSchemeGetsHttpAndAWarning()
    {
        var result = Parse("curl example.com/a");
        Assert.Equal("http://example.com/a", result.Request.Url);
        Assert.Contains(result.Warnings, w => w.Contains("no scheme", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstOfSeveralUrlsIsUsedWithAWarning()
    {
        var result = Parse("curl https://a/ --url https://b/ https://c/");
        Assert.Equal("https://a/", result.Request.Url);
        Assert.Contains(result.Warnings, w => w.Contains("https://b/ https://c/", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobEscapesAreRemovedUnlessGloboff()
    {
        Assert.Equal("https://x/a[1]{b}", Request("curl 'https://x/a\\[1\\]\\{b\\}'").Url);
        Assert.Equal("https://x/a\\[1\\]", Request("curl -g 'https://x/a\\[1\\]'").Url);
        var result = Parse("curl 'https://x/{a,b}'");
        Assert.Equal("https://x/{a,b}", result.Request.Url);
        Assert.Contains(result.Warnings, w => w.Contains("glob", StringComparison.Ordinal));
        Assert.Empty(Parse("curl 'http://[::1]:8080/'").Warnings);
    }

    [Fact]
    public void GetAppendsDataBeforeTheFragment()
    {
        Assert.Equal("https://x/p?a=1&b=2#f", Request("curl -G -d a=1 -d b=2 'https://x/p#f'").Url);
        Assert.Equal("https://x/p?k=v&a=1", Request("curl -G -d a=1 'https://x/p?k=v'").Url);
        Assert.Equal("https://x/p?a=1", Request("curl -G -d a=1 'https://x/p?'").Url);
    }

    // ---- headers ----

    [Fact]
    public void HeaderValueKeepsBlanksBeyondTheSeparatorSpace()
    {
        // curl sends the -H text verbatim, so "X:   y  " goes on the wire as "X:   y  ".
        Assert.Equal([("X", "  y  "), ("Y", "z"), ("Z", "\tw")], Headers(Request("curl https://x/ -H 'X:   y  ' -H 'Y:z' -H 'Z: \tw'")));
    }

    [Fact]
    public void SemicolonFormSendsAnEmptyHeaderAndColonFormSuppresses()
    {
        var request = Request("curl https://x/ -H 'Empty;' -H 'Gone:' -H 'Blank:   ' -H 'Trail;  '");
        Assert.Equal([("Empty", "")], Headers(request));
    }

    [Fact]
    public void DataGetsCurlsDefaultContentTypeAfterExplicitHeaders()
    {
        var request = Request("curl https://x/ -d a=1 -H 'X: 1'");
        Assert.Equal([("X", "1"), ("Content-Type", "application/x-www-form-urlencoded")], Headers(request));
        Assert.Equal(BodyMode.FormUrlEncoded, request.Body.Mode);
    }

    [Theory]
    [InlineData("-H 'Content-Type:'")]
    [InlineData("-H 'content-type:'")]
    [InlineData("-H 'Content-Type;  '")]
    public void SuppressedContentTypeLeavesTheBodyWithoutOne(string suppress)
    {
        var request = Request($"curl https://x/ -d a=1 {suppress}");
        Assert.Empty(request.Headers);
        Assert.Equal(BodyMode.Raw, request.Body.Mode);
        Assert.Equal("a=1", request.Body.Text);
    }

    [Fact]
    public void ExplicitContentTypeDecidesTheBodyMode()
    {
        Assert.Equal(BodyMode.Json, Request("curl https://x/ -d '{}' -H 'Content-Type: application/problem+json; charset=utf-8'").Body.Mode);
        Assert.Equal(BodyMode.Raw, Request("curl https://x/ -d 'x' -H 'Content-Type: text/plain'").Body.Mode);
        Assert.Equal(BodyMode.FormUrlEncoded, Request("curl https://x/ -d 'x' -H 'Content-type: Application/X-WWW-Form-Urlencoded'").Body.Mode);
    }

    [Fact]
    public void GeneratedHeadersKeepCommandLineOrderAndYieldToExplicitOnes()
    {
        var request = Request("curl https://x/ -H 'A: 1' -A agent -H 'B: 2' -e https://r/ -b c=1 -u u:p -H 'User-Agent: explicit'");
        Assert.Equal(
            [("A", "1"), ("B", "2"), ("Referer", "https://r/"), ("Cookie", "c=1"), ("Authorization", "Basic dTpw"), ("User-Agent", "explicit")],
            Headers(request));
    }

    [Fact]
    public void ExplicitSuppressionRemovesGeneratedHeader()
    {
        Assert.Empty(Request("curl https://x/ -b a=1 -H 'Cookie:'").Headers);
        Assert.Empty(Request("curl https://x/ -A '' ").Headers);
        Assert.Empty(Request("curl https://x/ -e ';auto'").Headers);
    }

    [Fact]
    public void LastUserAgentWinsAtTheFirstPosition()
    {
        Assert.Equal([("User-Agent", "two"), ("X", "1")], Headers(Request("curl https://x/ -A one -H 'X: 1' -A two")));
    }

    [Fact]
    public void UserWithoutPasswordWarns()
    {
        var result = Parse("curl -u svc https://x/");
        Assert.Equal("Basic c3ZjOg==", result.Request.GetHeader("Authorization"));
        Assert.Contains(result.Warnings, w => w.Contains("password", StringComparison.Ordinal));
    }

    [Fact]
    public void OAuth2BearerAddsAuthorization()
    {
        Assert.Equal("Bearer t0k", Request("curl --oauth2-bearer t0k https://x/").GetHeader("Authorization"));
    }

    [Fact]
    public void CookieWithoutEqualsIsACookieJarAndIsIgnored()
    {
        var result = Parse("curl -b cookies.txt https://x/");
        Assert.Empty(result.Request.Headers);
        Assert.Contains(result.Warnings, w => w.Contains("cookie-jar", StringComparison.Ordinal));
        Assert.Contains("-b", result.IgnoredOptions);
    }

    [Fact]
    public void PseudoHeaderAndMalformedHeaderAreIgnoredWithWarnings()
    {
        var result = Parse("curl https://x/ -H ':authority: x' -H 'NoColon'");
        Assert.Empty(result.Request.Headers);
        Assert.Equal(2, result.Warnings.Count);
    }

    // ---- data ----

    [Fact]
    public void DataPiecesJoinWithAmpersandAndJsonPiecesWithout()
    {
        var request = Request("curl https://x/ -d a=1 --json '{\"b\":2}' -d c=3 --data-raw @x --data-binary 'y z'");
        Assert.Equal("a=1{\"b\":2}&c=3&@x&y z", request.Body.Text);
        Assert.Equal([("Content-Type", "application/json"), ("Accept", "application/json")], Headers(request));
    }

    [Theory]
    [InlineData("hello world", "hello+world")]
    [InlineData("=a&b", "a%26b")]
    [InlineData("name=v w~.-_", "name=v+w~.-_")]
    [InlineData("n=é/?", "n=%C3%A9%2F%3F")]
    [InlineData("a@b=c", "a@b=c")]
    public void DataUrlEncodeForms(string argument, string expected)
    {
        Assert.Equal(expected, Request($"curl https://x/ --data-urlencode '{argument}'").Body.Text);
    }

    [Fact]
    public void JsonDoesNotOverrideExplicitHeaders()
    {
        var request = Request("curl https://x/ -H 'Accept:' -H 'Content-Type: text/plain' --json '{}'");
        Assert.Equal([("Content-Type", "text/plain")], Headers(request));
        Assert.Equal(BodyMode.Raw, request.Body.Mode);
    }

    [Fact]
    public void FormAndDataTogetherKeepTheFormAndWarn()
    {
        var result = Parse("curl https://x/ -F a=b -d c=d");
        Assert.Equal(BodyMode.Multipart, result.Request.Body.Mode);
        Assert.Contains(result.Warnings, w => w.Contains("-F", StringComparison.Ordinal));
    }

    // ---- forms ----

    [Fact]
    public void FormSyntax()
    {
        var parts = Request("curl https://x/ -F 'a=hello; world' -F 'b=\"q\\\"uo;te\";type=text/plain; charset=utf-8' -F 'c=  spaced  ' -F 'd=v;filename=f.txt' -F '=noname' --form-string 'e=<x;type=y'").Body.Parts;
        Assert.Equal(6, parts.Count);
        Assert.Equal(("a", "hello"), (parts[0].Name, parts[0].Value));
        Assert.Equal(("b", "q\"uo;te", "text/plain; charset=utf-8"), (parts[1].Name, parts[1].Value, parts[1].ContentType));
        Assert.Equal("spaced", parts[2].Value);
        Assert.Equal(("v", "f.txt"), (parts[3].Value, parts[3].FileName));
        Assert.Equal(("", "noname"), (parts[4].Name, parts[4].Value));
        Assert.Equal(("e", "<x;type=y", null), (parts[5].Name, parts[5].Value, parts[5].ContentType));
    }

    [Fact]
    public void FormFileAttributesInAnyOrder()
    {
        var parts = Request("curl https://x/ -F 'f=@\"a;b.txt\";filename=\"n\\\"m\";type=text/x' -F 'g=@c.bin;type=a/b;filename=z' -F 'h=<v.txt;type=text/csv'").Body.Parts;
        Assert.Equal(("a;b.txt", "n\"m", "text/x", false), (parts[0].FilePath, parts[0].FileName, parts[0].ContentType, parts[0].FileContentAsValue));
        Assert.Equal(("c.bin", "z", "a/b"), (parts[1].FilePath, parts[1].FileName, parts[1].ContentType));
        Assert.Equal(("v.txt", true, "text/csv"), (parts[2].FilePath, parts[2].FileContentAsValue, parts[2].ContentType));
    }

    [Fact]
    public void FormWithSeveralFilesBecomesSeveralPartsWithAWarning()
    {
        var result = Parse("curl https://x/ -F 'f=@a.txt,b.txt'");
        Assert.Equal(["a.txt", "b.txt"], result.Request.Body.Parts.Select(p => p.FilePath));
        Assert.Contains(result.Warnings, w => w.Contains("multipart/mixed", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownFormAttributeAndGarbageWarn()
    {
        var result = Parse("curl https://x/ -F 'a=\"v\"junk;foo=bar'");
        Assert.Equal("v", result.Request.Body.Parts[0].Value);
        Assert.Equal(2, result.Warnings.Count);
    }

    // ---- options ----

    [Fact]
    public void CompressedIsOffUnlessGiven()
    {
        Assert.False(Request("curl https://x/").Options.AutoDecompress);
        Assert.True(Request("curl --compressed https://x/").Options.AutoDecompress);
        Assert.False(Request("curl --compressed --no-compressed https://x/").Options.AutoDecompress);
    }

    [Fact]
    public void NoPrefixTurnsBooleansOff()
    {
        var options = Request("curl -L -k --no-location --no-insecure https://x/").Options;
        Assert.False(options.FollowRedirects);
        Assert.False(options.Insecure);
    }

    [Fact]
    public void TimeoutsAreDecimalSecondsAndZeroMeansNone()
    {
        var options = Request("curl -m 0.25 --connect-timeout 3 https://x/").Options;
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), options.ConnectTimeout);
        Assert.Null(Request("curl -m 0 https://x/").Options.Timeout);
        Assert.Contains(Parse("curl -m soon https://x/").Warnings, w => w.Contains("soon", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("proxy:8080", "http://proxy:8080")]
    [InlineData("socks5h://p:1080", "socks5h://p:1080")]
    [InlineData("''", null)]
    public void Proxy(string argument, string? expected)
    {
        Assert.Equal(expected, Request($"curl -x {argument} https://x/").Options.Proxy);
    }

    [Fact]
    public void ResolveForms()
    {
        var overrides = Request("curl --resolve '+a.example:443:[::1],10.0.0.1' --resolve '*:80:10.0.0.2' --resolve '[::2]:8443:10.0.0.3' https://x/").Options.ConnectOverrides;
        Assert.Equal(3, overrides.Count);
        Assert.Equal((ConnectOverrideKind.Resolve, "a.example", 443, "::1"), (overrides[0].Kind, overrides[0].Host, overrides[0].Port, overrides[0].TargetHost));
        Assert.Equal(("", 80, "10.0.0.2"), (overrides[1].Host, overrides[1].Port, overrides[1].TargetHost));
        Assert.Equal(("::2", 8443, "10.0.0.3"), (overrides[2].Host, overrides[2].Port, overrides[2].TargetHost));
    }

    [Fact]
    public void ConnectToForms()
    {
        var overrides = Request("curl --connect-to 'a.example:443:b.example:8443' --connect-to ':::' --connect-to '[::1]::[fe80::2]:' https://x/").Options.ConnectOverrides;
        Assert.Equal((ConnectOverrideKind.ConnectTo, "a.example", 443, "b.example", 8443), (overrides[0].Kind, overrides[0].Host, overrides[0].Port, overrides[0].TargetHost, overrides[0].TargetPort));
        Assert.Equal(("", 0, "", 0), (overrides[1].Host, overrides[1].Port, overrides[1].TargetHost, overrides[1].TargetPort));
        Assert.Equal(("::1", 0, "fe80::2", 0), (overrides[2].Host, overrides[2].Port, overrides[2].TargetHost, overrides[2].TargetPort));
    }

    [Fact]
    public void InvalidConnectOverridesWarn()
    {
        var result = Parse("curl --resolve 'a.example:x:1.2.3.4' --connect-to 'a:1:b' --resolve '-a.example:443' https://x/");
        Assert.Empty(result.Request.Options.ConnectOverrides);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Theory]
    [InlineData("C:\\a.pfx", "C:\\a.pfx", null, ClientCertificateSource.PfxFile)]
    [InlineData("C:\\certs\\a.pfx:pw", "C:\\certs\\a.pfx", "pw", ClientCertificateSource.PfxFile)]
    [InlineData("C:/certs/a.P12:p:w", "C:/certs/a.P12", "p:w", ClientCertificateSource.PfxFile)]
    [InlineData("cert.pem", "cert.pem", null, ClientCertificateSource.PemFile)]
    [InlineData("we\\:ird.pem:pw", "we:ird.pem", "pw", ClientCertificateSource.PemFile)]
    [InlineData("a\\\\b.pem", "a\\b.pem", null, ClientCertificateSource.PemFile)]
    [InlineData("\\\\\\\\server\\\\share\\\\c.pfx", "\\\\server\\share\\c.pfx", null, ClientCertificateSource.PfxFile)]
    [InlineData("C:relative.pem", "C", "relative.pem", ClientCertificateSource.PemFile)]
    public void CertParameter(string argument, string path, string? password, ClientCertificateSource source)
    {
        var cert = Request($"curl -E '{argument}' https://x/").Options.ClientCertificate;
        Assert.NotNull(cert);
        Assert.Equal((source, path, password), (cert.Source, cert.Path, cert.Password));
    }

    [Fact]
    public void CertTypeKeyAndPass()
    {
        var cert = Request("curl --cert client.crt --cert-type P12 --key k.pem --pass secret https://x/").Options.ClientCertificate!;
        Assert.Equal((ClientCertificateSource.PfxFile, "client.crt", "k.pem", "secret"), (cert.Source, cert.Path, cert.KeyPath, cert.Password));
    }

    [Fact]
    public void WindowsCertificateStore()
    {
        var cert = Request("curl -E 'CurrentUser\\MY\\0123456789abcdef0123456789abcdef01234567' https://x/").Options.ClientCertificate!;
        Assert.Equal((ClientCertificateSource.WindowsStore, "CurrentUser", "0123456789abcdef0123456789abcdef01234567", null),
            (cert.Source, cert.StoreLocation, cert.Thumbprint, cert.Path));
    }

    [Fact]
    public void HttpVersions()
    {
        Assert.Equal(HttpVersionPreference.Http11, Request("curl --http1.1 https://x/").HttpVersion);
        Assert.Equal(HttpVersionPreference.Http2, Request("curl --http2 https://x/").HttpVersion);
        Assert.Equal(HttpVersionPreference.Default, Request("curl https://x/").HttpVersion);
        var result = Parse("curl --http3 https://x/");
        Assert.Equal(HttpVersionPreference.Default, result.Request.HttpVersion);
        Assert.Contains(result.Warnings, w => w.Contains("HTTP/3", StringComparison.Ordinal));
    }

    [Fact]
    public void MaxRedirs()
    {
        var options = Request("curl -L --max-redirs 3 https://x/").Options;
        Assert.True(options.FollowRedirects);
        Assert.Equal(3, options.MaxRedirects);
        Assert.Equal(50, Request("curl -L https://x/").Options.MaxRedirects);
    }

    // ---- option syntax ----

    [Fact]
    public void ShortOptionBundlesAndAttachedArguments()
    {
        var result = Parse("curl -sSLkXPOST -H'A: 1' -d@- -ofile https://x/");
        Assert.Equal("POST", result.Request.Method);
        Assert.True(result.Request.Options.FollowRedirects);
        Assert.True(result.Request.Options.Insecure);
        Assert.Equal("1", result.Request.GetHeader("A"));
        Assert.Equal(["-s", "-S", "-o"], result.IgnoredOptions);
    }

    [Fact]
    public void ProgramMayBeAPath()
    {
        Assert.Equal("https://x/", Request("/usr/bin/curl https://x/").Url);
        Assert.Equal("https://x/", Request("C:\\Windows\\System32\\curl.exe https://x/").Url);
        Assert.Equal("https://x/", Request("& 'C:\\Program Files\\curl\\bin\\curl.exe' 'https://x/'").Url);
    }

    [Fact]
    public void ArgumentsWithoutCurlAreStillReadWithAWarning()
    {
        var result = Parse("-H 'A: 1' https://x/");
        Assert.Equal("https://x/", result.Request.Url);
        Assert.Contains(result.Warnings, w => w.Contains("does not start with curl", StringComparison.Ordinal));
    }

    [Fact]
    public void SecondCommandOnTheNextLineIsIgnored()
    {
        var result = Parse("curl https://a/\ncurl https://b/");
        Assert.Equal("https://a/", result.Request.Url);
        Assert.Contains(result.Warnings, w => w.Contains("Only the first command", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingContinuationIsToleratedWithAWarning()
    {
        var result = Parse("curl https://a/\n  -H 'A: 1'");
        Assert.Equal("1", result.Request.GetHeader("A"));
        Assert.Contains(result.Warnings, w => w.Contains("line continuation", StringComparison.Ordinal));
    }

    [Fact]
    public void NextStopsAtTheFirstRequest()
    {
        var result = Parse("curl https://a/ --next -X POST https://b/");
        Assert.Equal(("GET", "https://a/"), (result.Request.Method, result.Request.Url));
        Assert.Contains(result.Warnings, w => w.Contains("--next", StringComparison.Ordinal));
    }

    [Fact]
    public void VariablesExpandInExpandOptions()
    {
        var request = Request("curl --variable host=example.com --variable 'q=a b&c' --variable 'b64=aGk=' --expand-url 'https://{{host}}/?q={{q:url}}' --expand-header 'X-B: {{b64:64dec}} \\{{literal}}' --expand-data '{{q:json:trim}}'");
        Assert.Equal("https://example.com/?q=a%20b%26c", request.Url);
        Assert.Equal("hi {{literal}}", request.GetHeader("X-B"));
        Assert.Equal("a b&c", request.Body.Text);
    }

    [Fact]
    public void UnsetVariableAndEnvironmentImportWarn()
    {
        var result = Parse("curl --variable %HOME --variable '%MISSING=dflt' --expand-url 'https://x/{{HOME}}{{MISSING}}{{nope}}'");
        Assert.Equal("https://x/dflt", result.Request.Url);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public void EmptyInputGivesAnEmptyRequestAndAWarning()
    {
        var result = Parse("   ");
        Assert.Equal("", result.Request.Url);
        Assert.Contains(result.Warnings, w => w.Contains("No URL", StringComparison.Ordinal));
    }

    [Fact]
    public void NullInputThrows()
    {
        Assert.Throws<ArgumentNullException>(() => CurlParser.Parse(null!));
    }
}
