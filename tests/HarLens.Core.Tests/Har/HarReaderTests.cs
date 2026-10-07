using System.Text.Json;
using HarLens.Core.Har;
using HarLens.Core.Tests.Fixtures;

namespace HarLens.Core.Tests.Har;

public sealed class HarReaderTests
{
    [Fact]
    public void Chromium_fixture_indexes_list_columns_and_vendor_fields()
    {
        var result = HarReader.Load(FixturePaths.Har("chromium.har"));
        Assert.True(result.Success, result.FatalError);
        using var doc = result.Document!;
        Assert.Equal(11, doc.Entries.Count);
        Assert.Equal("1.2", doc.Log.Version);
        Assert.Equal("WebInspector", doc.Log.CreatorName);
        var page = Assert.Single(doc.Pages);
        Assert.Equal("page_1", page.Id);
        Assert.Equal(812.4, page.OnContentLoad);
        Assert.Equal(1544.9, page.OnLoad);

        var first = doc.Entries[0];
        Assert.Equal("GET", first.Method);
        Assert.Equal("https://www.contoso-shop.test/", first.Url);
        Assert.Equal(200, first.Status);
        Assert.Equal("http/2.0", first.ResponseHttpVersion);
        Assert.Equal("text/html", first.MimeType);
        Assert.Equal("document", first.ResourceType);
        Assert.Equal("VeryHigh", first.Priority);
        Assert.Equal("other", first.InitiatorType);
        Assert.Equal(1532, first.TransferSize);
        Assert.Equal("20261007T090000Z-17c8d8f9b6d", first.GetResponseHeader("X-Azure-Ref"));
        Assert.Equal(":authority", first.RequestHeaders[0].Name);
        Assert.Equal("203.0.113.10", first.ServerIPAddress);
        Assert.Equal("40121", first.Connection);
        Assert.True(first.ResponseBody.Exists);
        Assert.Equal(12.1, first.Timings.Dns);
        Assert.Equal(30.2, first.Timings.Ssl);
        Assert.Equal(48.3 - 30.2, first.Timings.TcpConnect, 6);

        var failed = doc.Entries.Single(e => e.Url.Contains("ads.tracker", StringComparison.Ordinal));
        Assert.True(failed.IsFailed);
        Assert.Equal("net::ERR_BLOCKED_BY_CLIENT", failed.Error);

        var cached = doc.Entries.Single(e => e.Url.EndsWith("pixel.png", StringComparison.Ordinal));
        Assert.True(cached.IsFromCache);
        Assert.Equal(BodyTextEncoding.Base64, cached.ResponseBody.Encoding);

        var ws = doc.Entries.Single(e => e.Url.StartsWith("wss:", StringComparison.Ordinal));
        Assert.Equal(3, ws.WebSocketMessageCount);
        Assert.Equal("websocket", ws.ResourceType);

        var notModified = doc.Entries.Single(e => e.Status == 304);
        Assert.True(notModified.HasCacheInfo);
        Assert.Equal("disk", notModified.FromCache);
    }

    [Theory]
    [InlineData("firefox.har", 3)]
    [InlineData("safari.har", 4)]
    [InlineData("proxy-export.har", 3)]
    [InlineData("har11.har", 3)]
    [InlineData("base64-bodies.har", 5)]
    [InlineData("compressed-bodies.har", 5)]
    [InlineData("minus-one.har", 2)]
    [InlineData("failures.har", 4)]
    [InlineData("filter.har", 12)]
    [InlineData("secrets.har", 8)]
    [InlineData("chromium.har.gz", 11)]
    [InlineData("bom.har", 3)]
    [InlineData("no-version.har", 3)]
    public void Fixture_loads(string name, int expectedEntries)
    {
        var result = HarReader.Load(FixturePaths.Har(name));
        Assert.True(result.Success, result.FatalError);
        using var doc = result.Document!;
        Assert.Equal(expectedEntries, doc.Entries.Count);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Firefox_security_state_and_sizes()
    {
        using var doc = HarReader.Load(FixturePaths.Har("firefox.har")).Document!;
        Assert.Equal("secure", doc.Entries[0].SecurityState);
        Assert.Equal(-1, doc.Entries[0].ResponseBodySize);
        Assert.Equal(412, doc.Entries[0].RequestHeadersSize);
        Assert.Equal("insecure", doc.Entries[2].SecurityState);
        Assert.True(doc.Entries[1].HasPostParams);
        Assert.True(doc.Entries[1].RequestBody.Exists);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero), doc.Pages[0].StartedDateTime.ToUniversalTime());
    }

    [Fact]
    public void Safari_fetch_type_marks_cache()
    {
        using var doc = HarReader.Load(FixturePaths.Har("safari.har")).Document!;
        Assert.False(doc.Entries[0].IsFromCache);
        Assert.True(doc.Entries[1].IsFromCache);
        Assert.True(doc.Entries[2].IsFromCache);
    }

    [Fact]
    public void Dates_with_seven_fraction_digits_and_offset()
    {
        using var doc = HarReader.Load(FixturePaths.Har("proxy-export.har")).Document!;
        var expected = new DateTimeOffset(2026, 10, 7, 10, 15, 2, TimeSpan.FromHours(1)).AddTicks(1234567);
        Assert.Equal(expected, doc.Entries[0].StartedDateTime);
        Assert.Equal("Proxy session 12", doc.Entries[0].Comment);
    }

    [Fact]
    public void Missing_version_warns()
    {
        var result = HarReader.Load(FixturePaths.Har("no-version.har"));
        Assert.Contains(result.Warnings, w => w.Contains("log.version", StringComparison.Ordinal));
    }

    [Fact]
    public void Truncated_file_recovers_complete_entries_and_reports_offset()
    {
        var expected = JsonDocument.Parse(File.ReadAllText(FixturePaths.Har("truncated.expected.json"))).RootElement;
        var result = HarReader.Load(FixturePaths.Har("truncated.har"));
        Assert.NotNull(result.Document);
        Assert.Equal(expected.GetProperty("recovered").GetInt32(), result.Document!.Entries.Count);
        var failure = Assert.IsType<HarParseFailure>(result.Failure);
        Assert.True(failure.Truncated);
        Assert.Equal(expected.GetProperty("recovered").GetInt32(), failure.RecoveredEntries);
        Assert.Equal(expected.GetProperty("length").GetInt64(), failure.ByteOffset);
        Assert.StartsWith("$.log.entries[4]", failure.JsonPath, StringComparison.Ordinal);
        result.Document.Dispose();
    }

    [Fact]
    public void Malformed_file_names_line_column_and_path()
    {
        var expected = JsonDocument.Parse(File.ReadAllText(FixturePaths.Har("malformed.expected.json"))).RootElement;
        var result = HarReader.Load(FixturePaths.Har("malformed.har"));
        var failure = Assert.IsType<HarParseFailure>(result.Failure);
        Assert.False(failure.Truncated);
        Assert.Equal(expected.GetProperty("offset").GetInt64(), failure.ByteOffset);
        Assert.Equal(expected.GetProperty("line").GetInt64(), failure.Line);
        Assert.Equal(expected.GetProperty("column").GetInt64(), failure.Column);
        Assert.Equal(expected.GetProperty("path").GetString(), failure.JsonPath);
        Assert.Equal(2, result.Document!.Entries.Count);
        result.Document.Dispose();
    }

    [Theory]
    [InlineData("not-har.json")]
    [InlineData("log-without-entries.json")]
    public void Valid_json_without_entries_is_a_clear_error(string name)
    {
        var result = HarReader.Load(FixturePaths.Har(name));
        Assert.False(result.Success);
        Assert.Contains("log.entries", result.FatalError, StringComparison.Ordinal);
    }

    [Fact]
    public void Garbage_reports_line_and_column()
    {
        var result = HarReader.LoadBytes("{\n  \"log\": {\n    \"version\": nope }"u8, "bad");
        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Equal(3, result.Failure!.Line);
        Assert.Equal(16, result.Failure.Column);
        Assert.Equal("$.log.version", result.Failure.JsonPath);
    }

    [Fact]
    public void Tiny_initial_buffer_boundaries_do_not_matter()
    {
        // Entries larger than the 1 MB initial buffer force the grow-and-retry path.
        var big = new string('a', 3 * 1024 * 1024);
        var json = $$$"""{"log":{"version":"1.2","creator":{"name":"t","version":"1"},"entries":[{"startedDateTime":"2026-01-01T00:00:00Z","time":1,"request":{"method":"GET","url":"https://x.test/","httpVersion":"HTTP/1.1","headers":[],"queryString":[],"cookies":[],"headersSize":-1,"bodySize":0},"response":{"status":200,"statusText":"OK","httpVersion":"HTTP/1.1","headers":[],"cookies":[],"content":{"size":3,"mimeType":"text/plain","text":"{{{big}}}"},"redirectURL":"","headersSize":-1,"bodySize":-1},"cache":{},"timings":{"send":0,"wait":1,"receive":0}},{"startedDateTime":"2026-01-01T00:00:01Z","time":1,"request":{"method":"POST","url":"https://x.test/2","httpVersion":"HTTP/1.1","headers":[],"queryString":[],"cookies":[],"headersSize":-1,"bodySize":0},"response":{"status":201,"statusText":"","httpVersion":"","headers":[],"cookies":[],"content":{"size":0,"mimeType":""},"redirectURL":"","headersSize":-1,"bodySize":-1},"cache":{},"timings":{"send":0,"wait":1,"receive":0}}]}}""";
        var result = HarReader.LoadBytes(System.Text.Encoding.UTF8.GetBytes(json), "big");
        Assert.True(result.Success, result.FatalError);
        Assert.Equal(2, result.Document!.Entries.Count);
        Assert.Equal(big.Length + 2, result.Document.Entries[0].ResponseBody.Length);
        Assert.Equal(201, result.Document.Entries[1].Status);
    }
}
