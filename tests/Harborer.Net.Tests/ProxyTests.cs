using System.Net;

namespace Harborer.Net.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    public const string Name = "Process-wide state";
}

/// <summary>The system proxy is never used; an explicit proxy is opt-in per request.</summary>
[Collection(GlobalStateCollection.Name)]
public sealed class SystemProxyTests
{
    [Fact]
    public async Task DefaultProxyIsIgnored()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("direct"));
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:9");
        try
        {
            var outcome = await Send.RunAsync(Send.Get(server.Url("/direct")));

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Equal("/direct", Assert.Single(server.Requests).Target);
            Assert.Equal(server.Port, outcome.Exchanges[0].RemotePort);
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
        }
    }
}

public sealed class ExplicitProxyTests
{
    [Fact]
    public async Task HttpUrlThroughProxyUsesAbsoluteForm()
    {
        await using var proxy = LoopbackServer.Start(ScriptedResponse.Text("via proxy"));
        var spec = Send.Get("http://example.test/p?q=1");
        spec.Options.Proxy = $"http://127.0.0.1:{proxy.Port}";

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var received = Assert.Single(proxy.Requests);
        Assert.Equal("http://example.test/p?q=1", received.Target);
        Assert.Equal("example.test", received.Header("Host"));
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal("via proxy", Send.Text(exchange.ResponseBody));
        Assert.Equal("127.0.0.1", exchange.RemoteAddress);
        Assert.StartsWith("GET http://example.test/p?q=1 HTTP/1.1", Send.Text(exchange.WireSent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyWithoutSchemeDefaultsToHttp()
    {
        await using var proxy = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get("http://example.test/");
        spec.Options.Proxy = $"127.0.0.1:{proxy.Port}";

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("http://example.test/", Assert.Single(proxy.Requests).Target);
    }

    [Fact]
    public async Task HttpsUrlThroughProxyTunnelsAndRecordsOnlyTheInnerExchange()
    {
        await using var proxy = LoopbackServer.Start(
            ScriptedResponse.Text("tunnelled"),
            new LoopbackServerOptions { Certificate = TestCertificates.Server, TunnelProxy = true });
        var spec = Send.Get("https://example.test/t");
        spec.Options.Proxy = $"http://127.0.0.1:{proxy.Port}";
        spec.Options.Insecure = true;

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var requests = proxy.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("CONNECT", requests[0].Method);
        Assert.Equal("example.test:443", requests[0].Target);
        Assert.Equal("/t", requests[1].Target);
        Assert.Equal("example.test", requests[1].Sni);

        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal("tunnelled", Send.Text(exchange.ResponseBody));
        Assert.StartsWith("GET /t HTTP/1.1", Send.Text(exchange.WireSent), StringComparison.Ordinal);
        Assert.Equal("CN=example.test", exchange.Tls?.Subject);
        Assert.True(exchange.Timings.Ssl >= 0, $"ssl {exchange.Timings.Ssl}");
        Assert.True(exchange.Timings.Connect >= exchange.Timings.Ssl);
    }

    [Fact]
    public async Task ConnectOverridesAreNotAppliedToTheProxyConnection()
    {
        await using var proxy = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get("http://example.test/");
        spec.Options.Proxy = $"http://127.0.0.1:{proxy.Port}";
        spec.Options.ConnectOverrides.Add(new Harborer.Core.Http.ConnectOverride
        {
            Kind = Harborer.Core.Http.ConnectOverrideKind.ConnectTo,
            TargetHost = "127.0.0.1",
            TargetPort = 9,
        });

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Single(proxy.Requests);
        Assert.Contains(outcome.Exchanges[0].Notices, n => n.StartsWith("Connect overrides are not applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedProxySchemeFailsBeforeConnecting()
    {
        var spec = Send.Get("http://example.test/");
        spec.Options.Proxy = "ftp://127.0.0.1:21";

        var outcome = await Send.RunAsync(spec);

        Assert.StartsWith("Unsupported proxy", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(outcome.Exchanges);
    }
}
