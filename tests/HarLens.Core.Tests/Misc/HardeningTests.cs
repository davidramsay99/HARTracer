using System.Diagnostics;
using System.Text;
using HarLens.Core.Har;
using HarLens.Core.Sanitize;
using HarLens.Core.Tests.Fixtures;
using HarLens.Core.Text;

namespace HarLens.Core.Tests.Misc;

/// <summary>Hostile bodies that once froze the UI thread, crashed the inspector, or leaked through the sanitizer.</summary>
public sealed class HardeningTests
{
    private static void Fast(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed.TotalSeconds:N1} s");
    }

    [Theory]
    [InlineData("<a>")]
    [InlineData("<a ")]
    [InlineData("<!--")]
    public void Html_indenter_is_linear_and_bounded(string fragment)
    {
        var html = string.Concat(Enumerable.Repeat(fragment, 60_000));
        string output = "";
        Fast(() => output = HtmlIndenter.Format(html));
        Assert.True(output.Length < html.Length * 30, $"output {output.Length} chars for {html.Length}");
    }

    [Fact]
    public void Jwt_finder_is_linear() =>
        Fast(() => _ = Jwt.FindAll(string.Concat(Enumerable.Repeat("eyJ-", 250_000)), "body").ToList());

    [Fact]
    public void Deep_json_is_left_unformatted()
    {
        var json = new string('[', 1001) + new string(']', 1001);
        Assert.False(JsonPretty.TryFormat(json, out var pretty));
        Assert.Equal(json, pretty);
    }

    [Fact]
    public void Deep_xml_is_left_unformatted()
    {
        var xml = string.Concat(Enumerable.Repeat("<a>", 5_000)) + string.Concat(Enumerable.Repeat("</a>", 5_000));
        Fast(() => Assert.False(XmlPretty.TryFormat(xml, out _)));
    }

    [Fact]
    public void Invalid_brotli_returns_null() =>
        Assert.Null(ContentDecoder.TryDecode(Encoding.ASCII.GetBytes("this is not brotli data at all"), "br", 1 << 20));

    [Fact]
    public void Sanitizer_redacts_custom_token_headers_camel_case_keys_and_structured_secrets()
    {
        const string har = """
            {"log":{"version":"1.2","creator":{"name":"t","version":"1"},"entries":[{
              "startedDateTime":"2026-01-01T00:00:00Z","time":1,
              "request":{"method":"POST","url":"https://x.test/t","httpVersion":"HTTP/1.1","cookies":[],
                "headers":[{"name":"X-Auth-Token","value":"header-secret-0001"},{"name":"Content-Type","value":"application/json"}],
                "queryString":[],"headersSize":-1,"bodySize":10,
                "postData":{"mimeType":"application/json","text":"{\"accessToken\":\"camel-secret-0002\",\"client_secret\":[\"array-secret-0003\"]}"}},
              "response":{"status":200,"statusText":"OK","httpVersion":"HTTP/1.1","cookies":[],"headers":[],
                "content":{"size":0,"mimeType":"text/plain"},"redirectURL":"","headersSize":-1,"bodySize":0},
              "cache":{},"timings":{"send":0,"wait":1,"receive":0}}]}}
            """;
        using var session = HarSession.FromDocument(HarReader.LoadBytes(Encoding.UTF8.GetBytes(har), "t").Document!);
        var path = Path.Combine(FixturePaths.Generated, $"hardening-{Guid.NewGuid():N}.har");
        new Sanitizer(new SanitizeOptions()).ExportToFile(session, path);
        var output = File.ReadAllText(path);
        File.Delete(path);
        foreach (var secret in new[] { "header-secret-0001", "camel-secret-0002", "array-secret-0003" })
        {
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        }
    }
}
