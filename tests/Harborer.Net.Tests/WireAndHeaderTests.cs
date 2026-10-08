using System.Text;
using Harborer.Core.Engine;
using Harborer.Core.Http;
using Harborer.Core.Model;

namespace Harborer.Net.Tests;

public sealed class WireAndHeaderTests
{
    private const string MixedHead =
        "HTTP/1.1 200 Fine Thanks\r\n" +
        "X-Mixed-Case: One\r\n" +
        "set-cookie: a=1\r\n" +
        "Set-Cookie: b=2\r\n" +
        "x-dup: 1\r\n" +
        "X-DUP: 2\r\n" +
        "content-length: 2\r\n" +
        "\r\n";

    [Fact]
    public async Task WireBytesAreExactlyWhatTheServerSawAndReceived()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse { Head = MixedHead, Body = "ok"u8.ToArray() });
        var spec = Send.Get(server.Url("/wire?q=1"), ("X-Test-Header", "value"), ("accept", "*/*"));
        spec.Method = "POST";
        spec.Body = RequestBody.FromText("hello");

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        var received = Assert.Single(server.Requests);
        Assert.Equal(received.RawBytes, exchange.WireSent);
        Assert.StartsWith("POST /wire?q=1 HTTP/1.1\r\n", Send.Text(exchange.WireSent), StringComparison.Ordinal);
        Assert.Equal(MixedHead + "ok", Send.Text(exchange.WireReceived));
        Assert.False(exchange.WireTruncated);
        Assert.Equal("hello"u8.ToArray(), exchange.RequestBody);
    }

    [Fact]
    public async Task ResponseHeadersKeepOrderCasingAndDuplicates()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse { Head = MixedHead, Body = "ok"u8.ToArray() });

        var outcome = await Send.RunAsync(Send.Get(server.Url("/")));

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(
            [
                new("X-Mixed-Case", "One"),
                new("set-cookie", "a=1"),
                new("Set-Cookie", "b=2"),
                new("x-dup", "1"),
                new("X-DUP", "2"),
                new("content-length", "2"),
            ],
            exchange.ResponseHeaders);
        Assert.Equal("Fine Thanks", exchange.StatusText);
        Assert.Equal("HTTP/1.1", exchange.HttpVersion);
    }

    [Fact]
    public async Task RequestHeadersAreTheOnesOnTheWire()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get(server.Url("/h"), ("X-Lower-case", "1"), ("X-Second", "2"));
        spec.Options.AutoDecompress = false;

        var outcome = await Send.RunAsync(spec);

        var exchange = Assert.Single(outcome.Exchanges);
        var received = Assert.Single(server.Requests);
        Assert.Equal(received.Headers.Select(h => new HarHeader(h.Key, h.Value)), exchange.RequestHeaders);
        Assert.Equal(new HarHeader("Host", $"127.0.0.1:{server.Port}"), exchange.RequestHeaders[0]);
        Assert.Contains(new HarHeader("X-Lower-case", "1"), exchange.RequestHeaders);
    }

    [Fact]
    public async Task TruncatedWireRecordFallsBackToMessageHeaders()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("a long enough body"));
        var spec = Send.Get(server.Url("/t"), ("X-One", "1"));

        var outcome = await Send.RunAsync(spec, new SendSettings { MaxRecordedWireBytes = 10 });

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.True(exchange.WireTruncated);
        Assert.Equal(10, exchange.WireSent.Length);
        Assert.Equal(10, exchange.WireReceived.Length);
        Assert.Contains(new HarHeader("X-One", "1"), exchange.RequestHeaders);
        Assert.Contains(exchange.RequestHeaders, h => h.Name == "Host");
        Assert.Contains(exchange.ResponseHeaders, h => h.Name == "Content-Type");
        Assert.Equal("a long enough body", Send.Text(exchange.ResponseBody));
        Assert.Contains(exchange.Notices, n => n.Contains("Wire recording truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledPseudoAndContentLengthHeadersAreNotSentAndHostIsEditable()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get(server.Url("/h"), (":authority", "x"), ("Content-Length", "999"), ("Host", "custom.test:1234"));
        spec.Headers.Add(new HeaderEntry("X-Disabled", "no", enabled: false));
        spec.Method = "PUT";
        spec.Body = RequestBody.FromText("abc");

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var received = Assert.Single(server.Requests);
        Assert.Equal("custom.test:1234", received.Header("Host"));
        Assert.Equal("3", received.Header("Content-Length"));
        Assert.False(received.HasHeader("X-Disabled"));
        Assert.DoesNotContain(received.Headers, h => h.Key.StartsWith(':'));
        Assert.Single(received.Headers, h => h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(outcome.Exchanges[0].Notices, n => n.Contains("Content-Length", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContentTypeWithoutBodyIsSentWithEmptyContent()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get(server.Url("/ct"), ("Content-Type", "application/xml"));

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var received = Assert.Single(server.Requests);
        Assert.Equal("application/xml", received.Header("Content-Type"));
        Assert.Empty(received.Body);
        Assert.Contains(outcome.Exchanges[0].Notices, n => n.Contains("empty body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidHeaderNameIsReportedAsANotice()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get(server.Url("/"), ("Bad Name", "x"), ("X-Line", "a\r\nInjected: 1"));

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var received = Assert.Single(server.Requests);
        Assert.False(received.HasHeader("Injected"));
        Assert.Contains(outcome.Exchanges[0].Notices, n => n.Contains("Bad Name", StringComparison.Ordinal));
        Assert.Contains(outcome.Exchanges[0].Notices, n => n.Contains("X-Line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnicodeHeaderValuesAreSentAsUtf8()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get(server.Url("/"), ("X-Name", "Zoë"));

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("Zoë", Assert.Single(server.Requests).Header("X-Name"));
        Assert.Contains(new HarHeader("X-Name", "Zoë"), outcome.Exchanges[0].RequestHeaders);
    }

    [Fact]
    public void ParserSkipsInterimResponsesAndUnfoldsContinuationLines()
    {
        byte[] wire = Encoding.ASCII.GetBytes(
            "HTTP/1.1 100 Continue\r\n\r\n" +
            "HTTP/1.1 201 Created\r\nX-A: 1\r\nX-Folded: first\r\n  second\r\n\r\nbody");

        var head = WireMessageParser.ParseFinalResponseHead(wire);

        Assert.NotNull(head);
        Assert.Equal(201, head.StatusCode);
        Assert.Equal("Created", head.ReasonPhrase);
        Assert.Equal([new HarHeader("X-A", "1"), new HarHeader("X-Folded", "first second")], head.Headers);
    }

    [Fact]
    public void ParserAcceptsBareLineFeedsAndRejectsIncompleteHeads()
    {
        var head = WireMessageParser.ParseFinalResponseHead("HTTP/1.0 404 Not Found\nA: b\n\n"u8);

        Assert.NotNull(head);
        Assert.Equal(404, head.StatusCode);
        Assert.Equal([new HarHeader("A", "b")], head.Headers);
        Assert.Null(WireMessageParser.ParseFinalResponseHead("HTTP/1.1 200 OK\r\nA: b\r\n"u8));
        Assert.Null(WireMessageParser.ParseRequestHead("not a request\r\n\r\n"u8));
    }
}
