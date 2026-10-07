using Harborer.Core.Http;
using Harborer.Core.Model;

namespace Harborer.Net.Tests;

public sealed class Http2Tests
{
    [Theory]
    [InlineData(HttpVersionPreference.Default)]
    [InlineData(HttpVersionPreference.Http2)]
    public async Task AlpnH2IsUsedAndRecorded(HttpVersionPreference version)
    {
        await using var server = new MinimalHttp2Server();
        var spec = Send.Get($"https://example.test:{server.Port}/h2?x=1", ("X-Custom", "Value"));
        spec.HttpVersion = version;
        spec.Options.Insecure = true;
        spec.Options.ConnectOverrides.Add(new ConnectOverride { Host = "example.test", TargetHost = "127.0.0.1" });

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal("HTTP/2", exchange.HttpVersion);
        Assert.Equal("h2", exchange.Tls?.ApplicationProtocol);
        Assert.Equal(200, exchange.Status);
        Assert.Equal(MinimalHttp2Server.ResponseText, Send.Text(exchange.ResponseBody));
        Assert.Equal(1, server.StreamsAnswered);

        Assert.Equal(
            [
                new(":method", "GET"),
                new(":authority", $"example.test:{server.Port}"),
                new(":scheme", "https"),
                new(":path", "/h2?x=1"),
            ],
            exchange.RequestHeaders.Take(4));
        Assert.Contains(new HarHeader("x-custom", "Value"), exchange.RequestHeaders);
        Assert.DoesNotContain(exchange.RequestHeaders, h => h.Name.Equals("host", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(new HarHeader("content-type", "text/plain"), exchange.ResponseHeaders);
        Assert.Contains(new HarHeader("x-h2", "yes"), exchange.ResponseHeaders);

        Assert.StartsWith("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", Send.Text(exchange.WireSent), StringComparison.Ordinal);
        Assert.NotEmpty(exchange.WireReceived);
        var timings = exchange.Timings;
        Assert.True(timings.Ssl >= 0 && timings.Send >= 0 && timings.Wait >= 0 && timings.Receive >= 0, $"{timings.Send} {timings.Wait} {timings.Receive}");
    }

    [Fact]
    public async Task Http2OverCleartextFallsBackToHttp11WithANotice()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("plain"));
        var spec = Send.Get(server.Url("/"));
        spec.HttpVersion = HttpVersionPreference.Http2;

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal("HTTP/1.1", exchange.HttpVersion);
        Assert.Contains(exchange.Notices, n => n.Contains("h2c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Http11PreferenceNeverNegotiatesHttp2()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("tls"), new LoopbackServerOptions { Certificate = TestCertificates.Server });
        var spec = Send.Get(server.Url("/"));
        spec.HttpVersion = HttpVersionPreference.Http11;
        spec.Options.Insecure = true;

        var outcome = await Send.RunAsync(spec);

        // SocketsHttpHandler sends no ALPN extension for an exact HTTP/1.1 request.
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("HTTP/1.1", outcome.Exchanges[0].HttpVersion);
        Assert.Null(outcome.Exchanges[0].Tls?.ApplicationProtocol);
    }
}
