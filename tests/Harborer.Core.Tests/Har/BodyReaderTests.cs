using Harborer.Core.Har;
using Harborer.Core.Http;
using Harborer.Core.Tests.Fixtures;

namespace Harborer.Core.Tests.Har;

public sealed class BodyReaderTests
{
    [Fact]
    public void Base64_binary_and_text_bodies_decode()
    {
        using var doc = HarReader.Load(FixturePaths.Har("base64-bodies.har")).Document!;
        var png = BodyReader.Read(doc.Entries[0], BodySide.Response)!;
        Assert.False(png.IsText);
        Assert.True(png.WasBase64);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png.Bytes[..4]);

        var json = BodyReader.Read(doc.Entries[2], BodySide.Response)!;
        Assert.Equal("{\"encoded\":\"base64\",\"ok\":true}", json.Text);
        Assert.Equal("{\"encoded\":\"base64\",\"ok\":true}", BodyReader.ReadSearchableText(doc.Entries[2], BodySide.Response));

        var bin = BodyReader.Read(doc.Entries[3], BodySide.Response)!;
        Assert.Equal(256, bin.Bytes.Length);
        Assert.Null(BodyReader.ReadSearchableText(doc.Entries[3], BodySide.Response));

        var post = BodyReader.Read(doc.Entries[4], BodySide.Request)!;
        Assert.Equal(new byte[] { 0, 1, (byte)'b', (byte)'i', (byte)'n', (byte)'a', (byte)'r', (byte)'y', 0xFF }, post.Bytes);
    }

    [Theory]
    [InlineData(0, "none", false)]
    [InlineData(1, "gzip", true)]
    [InlineData(2, "deflate", true)]
    [InlineData(3, "brotli", true)]
    public void Stored_compressed_bodies_are_decoded(int index, string marker, bool expectNotice)
    {
        using var doc = HarReader.Load(FixturePaths.Har("compressed-bodies.har")).Document!;
        var body = BodyReader.Read(doc.Entries[index], BodySide.Response)!;
        Assert.NotNull(body.Text);
        Assert.Contains($"\"compressed\":\"{marker}\"", body.Text, StringComparison.Ordinal);
        Assert.Equal(expectNotice, body.Notices.Count > 0);
    }

    [Fact]
    public void Zstd_is_shown_undecoded_with_notice()
    {
        using var doc = HarReader.Load(FixturePaths.Har("compressed-bodies.har")).Document!;
        var body = BodyReader.Read(doc.Entries[4], BodySide.Response)!;
        Assert.Contains(body.Notices, n => n.Contains("Zstandard", StringComparison.Ordinal));
        Assert.Equal(0x28, body.Bytes[0]);
    }

    [Fact]
    public void Escaped_text_is_unescaped()
    {
        using var doc = HarReader.Load(FixturePaths.Har("chromium.har")).Document!;
        var cart = doc.Entries.Single(e => e.Url.Contains("/api/cart", StringComparison.Ordinal));
        var body = BodyReader.Read(cart, BodySide.Response)!;
        Assert.StartsWith("{\"cart\":", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_is_bounded_and_returns_same_instance()
    {
        using var doc = HarReader.Load(FixturePaths.Har("chromium.har")).Document!;
        var cache = new BodyCache(capacityBytes: 2_000);
        var first = cache.Get(doc.Entries[0], BodySide.Response);
        Assert.Same(first, cache.Get(doc.Entries[0], BodySide.Response));
        foreach (var e in doc.Entries)
        {
            cache.Get(e, BodySide.Response);
        }

        Assert.True(cache.CurrentBytes <= 2_000);
    }

    [Fact]
    public void Entry_detail_exposes_query_cookies_websocket_and_raw()
    {
        using var doc = HarReader.Load(FixturePaths.Har("chromium.har")).Document!;
        using (var cart = EntryDetail.Load(doc.Entries.Single(e => e.Url.Contains("/api/cart", StringComparison.Ordinal))))
        {
            Assert.Equal(["currency", "include"], cart.QueryString.Select(q => q.Name));
            Assert.Equal("script", cart.Initiator!.Value.GetProperty("type").GetString());
        }

        using (var home = EntryDetail.Load(doc.Entries[0]))
        {
            Assert.Equal(["session", "theme"], home.RequestCookies.Select(c => c.Name));
            var set = Assert.Single(home.ResponseCookies);
            Assert.True(set.HttpOnly);
            Assert.True(set.Secure);
            Assert.Equal("Lax", set.SameSite);
            Assert.Equal("/", set.Path);
            Assert.Contains("\"_initiator\"", home.FormatRawJson(), StringComparison.Ordinal);
            Assert.Equal("VeryHigh", home.GetField("_priority"));
            Assert.Equal("1532", home.GetField("_transferSize"));
            Assert.Equal("1532", home.GetField("response._transferSize"));
        }

        using var ws = EntryDetail.Load(doc.Entries.Single(e => e.WebSocketMessageCount > 0));
        var frames = ws.WebSocketMessages;
        Assert.Equal(3, frames.Count);
        Assert.True(frames[0].IsSend);
        Assert.Equal("binary", frames[2].OpcodeName);
    }

    [Fact]
    public void Raw_message_reconstruction()
    {
        using var doc = HarReader.Load(FixturePaths.Har("chromium.har")).Document!;
        var post = doc.Entries.Single(e => e.Method == "POST");
        var text = RawMessage.Request(post, BodyReader.Read(post, BodySide.Request));
        Assert.StartsWith("POST /api/checkout HTTP/1.1\r\nHost: www.contoso-shop.test\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n{\"cartId\":\"c-1001\",\"payment\":\"card\"}", text, StringComparison.Ordinal);

        var h2 = doc.Entries[0];
        var raw = RawMessage.Request(h2, null);
        Assert.StartsWith("GET / HTTP/2\r\nHost: www.contoso-shop.test\r\naccept: */*", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(":authority", raw, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/2 200 OK\r\n", RawMessage.Response(h2, null), StringComparison.Ordinal);
    }
}
