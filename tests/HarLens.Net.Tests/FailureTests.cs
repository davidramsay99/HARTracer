using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HarLens.Core.Engine;
using HarLens.Core.Http;

namespace HarLens.Net.Tests;

/// <summary>Cancel, timeouts and network failures: SendAsync reports them and never throws.</summary>
public sealed class FailureTests
{
    [Fact]
    public async Task CancelAbortsAWaitingRequest()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        var outcome = await Send.RunAsync(Send.Get(server.Url("/slow")), cancellationToken: cancel.Token);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
        Assert.True(outcome.Cancelled);
        Assert.False(outcome.Succeeded);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal("Cancelled", exchange.Error);
        Assert.Equal(0, exchange.Status);
        Assert.StartsWith("GET /slow HTTP/1.1", Send.Text(exchange.WireSent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelDuringTheBodyKeepsThePartialExchange()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse
        {
            Body = new byte[100],
            StallAfterBodyBytes = 10,
            StallFor = TimeSpan.FromSeconds(30),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var outcome = await Send.RunAsync(Send.Get(server.Url("/partial")), cancellationToken: cancel.Token);

        Assert.True(outcome.Cancelled);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal("Cancelled", exchange.Error);
        Assert.Equal(200, exchange.Status);
        Assert.Equal(10, exchange.ResponseBody.Length);
        Assert.Contains(exchange.ResponseHeaders, h => h.Name == "Content-Length");
    }

    [Fact]
    public async Task WholeRequestTimeoutIsReportedLikeCurl()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse { Delay = TimeSpan.FromSeconds(30) });
        var spec = Send.Get(server.Url("/slow"));
        spec.Options.Timeout = TimeSpan.FromMilliseconds(300);
        var stopwatch = Stopwatch.StartNew();

        var outcome = await Send.RunAsync(spec);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
        Assert.False(outcome.Cancelled);
        Assert.Matches(@"^Operation timed out after \d+ milliseconds with 0 bytes received$", outcome.Error);
        Assert.Equal(outcome.Error, Assert.Single(outcome.Exchanges).Error);
    }

    [Fact]
    public async Task TimeoutCoversTheBodyToo()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse
        {
            Body = new byte[100],
            StallAfterBodyBytes = 40,
            StallFor = TimeSpan.FromSeconds(30),
        });
        var spec = Send.Get(server.Url("/partial"));
        spec.Options.Timeout = TimeSpan.FromMilliseconds(500);

        var outcome = await Send.RunAsync(spec);

        Assert.Matches(@"^Operation timed out after \d+ milliseconds with 40 bytes received$", outcome.Error);
        Assert.Equal(40, Assert.Single(outcome.Exchanges).ResponseBody.Length);
    }

    [Fact]
    public async Task ConnectTimeoutCoversTheTlsHandshake()
    {
        // Accepts TCP but never answers the ClientHello, so connection setup cannot finish.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var held = new List<Socket>();
        var accept = Task.Run(async () =>
        {
            try
            {
                held.Add(await listener.AcceptSocketAsync());
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        });
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var spec = Send.Get($"https://127.0.0.1:{port}/");
            spec.Options.ConnectTimeout = TimeSpan.FromMilliseconds(300);
            var stopwatch = Stopwatch.StartNew();

            var outcome = await Send.RunAsync(spec);

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
            Assert.Matches(@"^Connection timed out after \d+ milliseconds$", outcome.Error);
            Assert.False(outcome.Cancelled);
            var exchange = Assert.Single(outcome.Exchanges);
            Assert.Equal("127.0.0.1", exchange.RemoteAddress);
            Assert.True(exchange.Timings.Connect < 0 || exchange.Timings.Ssl < 0);
        }
        finally
        {
            listener.Stop();
            await accept;
            held.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public async Task ConnectionRefusedIsReported()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var outcome = await Send.RunAsync(Send.Get($"http://127.0.0.1:{port}/"));

        Assert.Matches($@"^Failed to connect to 127\.0\.0\.1 port {port} after \d+ ms: Connection refused$", outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(0, exchange.Status);
        Assert.Contains(exchange.RequestHeaders, h => h.Name == "Host");
        Assert.Empty(exchange.WireSent);
    }

    [Fact]
    public async Task ServerClosingWithoutAResponseIsAnEmptyReply()
    {
        await using var server = LoopbackServer.Start(new ScriptedResponse { CloseWithoutResponse = true });

        var outcome = await Send.RunAsync(Send.Get(server.Url("/")));

        Assert.Equal("Empty reply from server", outcome.Error);
    }

    [Theory]
    [InlineData("ftp://example.test/file")]
    [InlineData("example.test/no-scheme")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public async Task OnlyAbsoluteHttpUrlsAreSent(string url)
    {
        var outcome = await Send.RunAsync(Send.Get(url));

        Assert.NotNull(outcome.Error);
        Assert.Empty(outcome.Exchanges);
    }

    [Fact]
    public async Task InvalidMethodFailsBeforeConnecting()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("never"));
        var spec = Send.Get(server.Url("/"));
        spec.Method = "GET THIS";

        var outcome = await Send.RunAsync(spec);

        Assert.StartsWith("Invalid method", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task CustomMethodIsSentAsIs()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get(server.Url("/"));
        spec.Method = "PROPFIND";

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("PROPFIND", Assert.Single(server.Requests).Method);
        Assert.Equal("PROPFIND", outcome.Exchanges[0].Method);
    }

    [Fact]
    public async Task ConcurrentSendsOnOneEngineAreIndependent()
    {
        await using var server = LoopbackServer.Start(request => ScriptedResponse.Text(request.Path));
        var engine = new RequestEngine();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            engine.SendAsync(Send.Get(server.Url("/" + i)), new SendSettings(), CancellationToken.None)));

        for (int i = 0; i < outcomes.Length; i++)
        {
            Assert.Equal("/" + i, Send.Text(Assert.Single(outcomes[i].Exchanges).ResponseBody));
        }
    }
}
