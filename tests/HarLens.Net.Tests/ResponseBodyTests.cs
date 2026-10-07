using System.IO.Compression;
using System.Text;
using HarLens.Core.Engine;
using HarLens.Core.Model;

namespace HarLens.Net.Tests;

public sealed class ResponseBodyTests
{
    private static readonly byte[] s_plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("HarLens decodes bodies. ", 200)));

    private static byte[] Compress(string encoding, byte[] data)
    {
        using var output = new MemoryStream();
        using (Stream encoder = encoding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "zlib" => new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true),
            _ => new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true),
        })
        {
            encoder.Write(data);
        }

        return output.ToArray();
    }

    private static ScriptedResponse Encoded(string encoding, byte[] body) => new()
    {
        Headers = [new("Content-Type", "text/plain"), new("Content-Encoding", encoding)],
        Body = body,
    };

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task CompressedBodyIsDecodedAndTheHeaderKept(string encoding)
    {
        byte[] wire = Compress(encoding, s_plain);
        await using var server = LoopbackServer.Start(Encoded(encoding, wire));

        var outcome = await Send.RunAsync(Send.Get(server.Url("/z")));

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(s_plain, exchange.ResponseBody);
        Assert.True(exchange.ResponseBodyDecompressed);
        Assert.Equal(wire.Length, exchange.ResponseBodyWireSize);
        Assert.Contains(new HarHeader("Content-Encoding", encoding), exchange.ResponseHeaders);
        Assert.Equal(RequestMessageFactory.AutoAcceptEncoding, Assert.Single(server.Requests).Header("Accept-Encoding"));
    }

    [Fact]
    public async Task ExistingAcceptEncodingIsNotReplaced()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));

        await Send.RunAsync(Send.Get(server.Url("/"), ("accept-encoding", "identity")));

        var received = Assert.Single(server.Requests);
        Assert.Single(received.Headers, h => h.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("identity", received.Header("Accept-Encoding"));
    }

    [Fact]
    public async Task AutoDecompressOffLeavesBytesUntouchedAndAddsNoAcceptEncoding()
    {
        byte[] wire = Compress("gzip", s_plain);
        await using var server = LoopbackServer.Start(Encoded("gzip", wire));
        var spec = Send.Get(server.Url("/z"));
        spec.Options.AutoDecompress = false;

        var outcome = await Send.RunAsync(spec);

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(wire, exchange.ResponseBody);
        Assert.False(exchange.ResponseBodyDecompressed);
        Assert.False(Assert.Single(server.Requests).HasHeader("Accept-Encoding"));
    }

    [Fact]
    public async Task ZstdStaysUndecodedWithANotice()
    {
        byte[] body = [0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3];
        await using var server = LoopbackServer.Start(Encoded("zstd", body));

        var outcome = await Send.RunAsync(Send.Get(server.Url("/z")));

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(body, exchange.ResponseBody);
        Assert.False(exchange.ResponseBodyDecompressed);
        Assert.Contains("Zstandard bodies are displayed as undecoded bytes", exchange.Notices);
    }

    [Fact]
    public async Task ResponseLargerThanTheCapIsTruncatedWithANotice()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse { Body = new byte[10_000] });

        var outcome = await Send.RunAsync(Send.Get(server.Url("/big")), new SendSettings { ResponseSizeCap = 1000 });

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(1000, exchange.ResponseBody.Length);
        Assert.Equal(1000, exchange.ResponseBodyWireSize);
        Assert.True(exchange.ResponseBodyTruncated);
        Assert.Contains(exchange.Notices, n => n.Contains("truncated at 1000 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResponseExactlyAtTheCapIsNotTruncated()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse { Body = new byte[1000] });

        var outcome = await Send.RunAsync(Send.Get(server.Url("/exact")), new SendSettings { ResponseSizeCap = 1000 });

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(1000, exchange.ResponseBody.Length);
        Assert.False(exchange.ResponseBodyTruncated);
    }

    [Fact]
    public async Task DecodedBodyIsCappedToo()
    {
        byte[] wire = Compress("gzip", s_plain);
        await using var server = LoopbackServer.Start(Encoded("gzip", wire));

        var outcome = await Send.RunAsync(Send.Get(server.Url("/z")), new SendSettings { ResponseSizeCap = 2000 });

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(2000, exchange.ResponseBody.Length);
        Assert.Equal(s_plain[..2000], exchange.ResponseBody);
        Assert.True(exchange.ResponseBodyTruncated);
    }

    [Fact]
    public async Task CompressedBodyCutByTheCapDecodesToAPrefix()
    {
        byte[] noisy = new byte[20_000];
        new Random(7).NextBytes(noisy);
        byte[] wire = Compress("gzip", noisy);
        await using var server = LoopbackServer.Start(Encoded("gzip", wire));

        var outcome = await Send.RunAsync(Send.Get(server.Url("/z")), new SendSettings { ResponseSizeCap = 5000 });

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.True(exchange.ResponseBodyTruncated);
        Assert.Equal(5000, exchange.ResponseBodyWireSize);
        Assert.True(exchange.ResponseBodyDecompressed);
        Assert.InRange(exchange.ResponseBody.Length, 1, 5000);
        Assert.Equal(noisy[..exchange.ResponseBody.Length], exchange.ResponseBody);
        Assert.Contains(exchange.Notices, n => n.Contains("partial", StringComparison.Ordinal));
    }

    [Fact]
    public void DeflateAcceptsZlibWrappedAndRawStreams()
    {
        foreach (string kind in new[] { "zlib", "raw" })
        {
            var result = ContentDecoder.Decode(Compress(kind, s_plain), ["deflate"], long.MaxValue, false);

            Assert.True(result.Decoded, kind);
            Assert.Equal(s_plain, result.Body);
        }
    }

    [Fact]
    public void ChainedEncodingsAreDecodedInReverseOrder()
    {
        byte[] wire = Compress("br", Compress("gzip", s_plain));

        var result = ContentDecoder.Decode(wire, ContentDecoder.ParseEncodings(["gzip, br"]), long.MaxValue, false);

        Assert.True(result.Decoded);
        Assert.Equal(s_plain, result.Body);
    }

    [Fact]
    public void CorruptBodyIsKeptAsReceived()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8];

        var result = ContentDecoder.Decode(garbage, ["gzip"], long.MaxValue, false);

        Assert.False(result.Decoded);
        Assert.Equal(garbage, result.Body);
        Assert.Single(result.Notices);
    }

    [Fact]
    public void IdentityAndEmptyTokensAreIgnored()
    {
        Assert.Equal(["gzip"], ContentDecoder.ParseEncodings(["identity, ", " GZIP"]));
    }
}
