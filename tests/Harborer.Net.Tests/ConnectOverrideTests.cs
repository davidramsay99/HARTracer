using Harborer.Core.Http;

namespace Harborer.Net.Tests;

/// <summary>The connect override reaches the loopback server with the URL host in Host and SNI.</summary>
public sealed class ConnectOverrideTests
{
    private static readonly LoopbackServerOptions s_tls = new() { Certificate = TestCertificates.Server };

    [Fact]
    public async Task ResolveOverrideKeepsUrlHostInHostHeaderAndSni()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("hello"), s_tls);
        var spec = Send.Get($"https://example.test:{server.Port}/p");
        spec.Options.Insecure = true;
        spec.Options.ConnectOverrides.Add(new ConnectOverride
        {
            Kind = ConnectOverrideKind.Resolve,
            Host = "example.test",
            Port = server.Port,
            TargetHost = "127.0.0.1",
        });

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(200, exchange.Status);
        Assert.Equal("hello", Send.Text(exchange.ResponseBody));
        Assert.Equal("127.0.0.1", exchange.RemoteAddress);
        Assert.Equal(server.Port, exchange.RemotePort);
        Assert.Equal(-1, exchange.Timings.Dns);
        Assert.Equal("example.test", exchange.Tls?.ServerName);

        var received = Assert.Single(server.Requests);
        Assert.Equal($"example.test:{server.Port}", received.Header("Host"));
        Assert.Equal("example.test", received.Sni);
        Assert.Equal("/p", received.Target);
    }

    [Fact]
    public async Task ConnectToOverrideMapsDefaultPortToLoopbackPort()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("hello"), s_tls);
        var spec = Send.Get("https://example.test/p");
        spec.Options.Insecure = true;
        spec.Options.ConnectOverrides.Add(new ConnectOverride
        {
            Kind = ConnectOverrideKind.ConnectTo,
            Host = "example.test",
            Port = 443,
            TargetHost = "127.0.0.1",
            TargetPort = server.Port,
        });

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal("https://example.test/p", exchange.Url);
        Assert.Equal(server.Port, exchange.RemotePort);
        var received = Assert.Single(server.Requests);
        Assert.Equal("example.test", received.Header("Host"));
        Assert.Equal("example.test", received.Sni);
        Assert.Contains(exchange.Notices, n => n.Contains("Connect override", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverrideForAnotherHostIsNotApplied()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("direct"));
        var spec = Send.Get(server.Url("/direct"));
        spec.Options.ConnectOverrides.Add(new ConnectOverride
        {
            Kind = ConnectOverrideKind.ConnectTo,
            Host = "other.test",
            TargetHost = "127.0.0.2",
            TargetPort = 9,
        });

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ResolveOverrideAcceptsAnAddressList()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var spec = Send.Get($"http://multi.test:{server.Port}/");
        spec.Options.ConnectOverrides.Add(new ConnectOverride
        {
            Kind = ConnectOverrideKind.Resolve,
            Host = "multi.test",
            Port = server.Port,
            TargetHost = "127.0.0.1,127.0.0.1",
        });

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("multi.test:" + server.Port, Assert.Single(server.Requests).Header("Host"));
    }
}
