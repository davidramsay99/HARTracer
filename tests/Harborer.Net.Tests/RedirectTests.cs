using Harborer.Core.Http;
using Harborer.Core.Model;

namespace Harborer.Net.Tests;

/// <summary>Redirects follow curl -L semantics.</summary>
public sealed class RedirectTests
{
    private static LoopbackServer StartRedirectServer() => LoopbackServer.Start(request => request.Path switch
    {
        "/302" => ScriptedResponse.Redirect(302, "/final"),
        "/301" => ScriptedResponse.Redirect(301, "/final"),
        "/303" => ScriptedResponse.Redirect(303, "/final"),
        "/307" => ScriptedResponse.Redirect(307, "/final"),
        "/308" => ScriptedResponse.Redirect(308, "final"),
        "/loop" => ScriptedResponse.Redirect(302, "/loop"),
        "/ftp" => ScriptedResponse.Redirect(302, "ftp://example.test/file"),
        _ when request.Path.StartsWith("/cross", StringComparison.Ordinal) =>
            ScriptedResponse.Redirect(302, request.Path["/cross".Length..].TrimStart('?')),
        _ => ScriptedResponse.Text("final " + request.Method + " " + request.BodyText),
    });

    private static HttpRequestSpec Post(string url, string body)
    {
        var spec = Send.Get(url, ("Content-Type", "text/plain"));
        spec.Method = "POST";
        spec.Body = RequestBody.FromText(body);
        return spec;
    }

    [Fact]
    public async Task RedirectNotFollowedByDefaultReturnsTheRedirectItself()
    {
        await using var server = StartRedirectServer();

        var outcome = await Send.RunAsync(Send.Get(server.Url("/302")));

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(302, exchange.Status);
        Assert.Equal("Found", exchange.StatusText);
        Assert.Equal("/final", exchange.RedirectLocation);
        Assert.Contains(new HarHeader("Location", "/final"), exchange.ResponseHeaders);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task FollowedRedirectRecordsEachHopSeparately()
    {
        await using var server = StartRedirectServer();
        var spec = Send.Get(server.Url("/302"));
        spec.Options.FollowRedirects = true;

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(2, outcome.Exchanges.Count);
        Assert.Equal(302, outcome.Exchanges[0].Status);
        Assert.Equal(200, outcome.Exchanges[1].Status);
        Assert.Equal(server.Url("/final"), outcome.Exchanges[1].Url);
        Assert.Equal("final GET ", Send.Text(outcome.Exchanges[1].ResponseBody));

        // A fresh handler per hop: each hop has its own connection and connect timing.
        Assert.Equal(2, server.ConnectionCount);
        Assert.All(outcome.Exchanges, e => Assert.True(e.Timings.Connect >= 0));
    }

    [Theory]
    [InlineData("/301")]
    [InlineData("/302")]
    [InlineData("/303")]
    public async Task PostBecomesGetWithoutBody(string path)
    {
        await using var server = StartRedirectServer();
        var spec = Post(server.Url(path), "payload");
        spec.Options.FollowRedirects = true;

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var requests = server.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("POST", requests[0].Method);
        Assert.Equal("payload", requests[0].BodyText);
        Assert.Equal("GET", requests[1].Method);
        Assert.Empty(requests[1].Body);
        Assert.False(requests[1].HasHeader("Content-Type"));
        Assert.Equal("GET", outcome.Exchanges[1].Method);
        Assert.Null(outcome.Exchanges[1].RequestBody);
        Assert.Contains(outcome.Exchanges[1].Notices, n => n.Contains("changed to GET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeeOtherTurnsPutIntoGetButMovedKeepsPut()
    {
        await using var server = StartRedirectServer();
        var see = Post(server.Url("/303"), "x");
        see.Method = "PUT";
        see.Options.FollowRedirects = true;
        var moved = Post(server.Url("/301"), "y");
        moved.Method = "PUT";
        moved.Options.FollowRedirects = true;

        var seeOutcome = await Send.RunAsync(see);
        var movedOutcome = await Send.RunAsync(moved);

        Assert.Equal("GET", seeOutcome.Exchanges[1].Method);
        Assert.Equal("PUT", movedOutcome.Exchanges[1].Method);
        Assert.Equal("final PUT y", Send.Text(movedOutcome.Exchanges[1].ResponseBody));
    }

    [Theory]
    [InlineData("/307")]
    [InlineData("/308")]
    public async Task TemporaryAndPermanentRedirectKeepMethodAndBody(string path)
    {
        await using var server = StartRedirectServer();
        var spec = Post(server.Url(path), "keep me");
        spec.Options.FollowRedirects = true;

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var second = server.Requests.ToArray()[1];
        Assert.Equal("POST", second.Method);
        Assert.Equal("keep me", second.BodyText);
        Assert.Equal("text/plain", second.Header("Content-Type"));
        Assert.Equal("/final", second.Path);
    }

    [Fact]
    public async Task CredentialsAreDroppedWhenTheHostChangesAndKeptOnTheSameHost()
    {
        await using var server = StartRedirectServer();
        string other = $"http://b.test:{server.Port}/final";
        var spec = Send.Get(
            $"http://a.test:{server.Port}/cross?/cross?{other}",
            ("Authorization", "Bearer secret"),
            ("Cookie", "session=1"),
            ("X-Custom", "kept"));
        spec.Options.FollowRedirects = true;
        foreach (string host in new[] { "a.test", "b.test" })
        {
            spec.Options.ConnectOverrides.Add(new ConnectOverride { Host = host, Port = server.Port, TargetHost = "127.0.0.1" });
        }

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var requests = server.Requests.ToArray();
        Assert.Equal(3, requests.Length);

        // Hop 1 and 2 stay on a.test, hop 3 goes to b.test.
        Assert.Equal("Bearer secret", requests[0].Header("Authorization"));
        Assert.Equal("Bearer secret", requests[1].Header("Authorization"));
        Assert.Equal("session=1", requests[1].Header("Cookie"));
        Assert.Equal($"b.test:{server.Port}", requests[2].Header("Host"));
        Assert.False(requests[2].HasHeader("Authorization"));
        Assert.False(requests[2].HasHeader("Cookie"));
        Assert.Equal("kept", requests[2].Header("X-Custom"));
        Assert.Contains(outcome.Exchanges[2].Notices, n => n.Contains("Authorization, Proxy-Authorization, Cookie and Host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaxRedirectsIsEnforcedLikeCurl()
    {
        await using var server = StartRedirectServer();
        var spec = Send.Get(server.Url("/loop"));
        spec.Options.FollowRedirects = true;
        spec.Options.MaxRedirects = 3;

        var outcome = await Send.RunAsync(spec);

        Assert.Equal("Maximum (3) redirects followed", outcome.Error);
        Assert.False(outcome.Cancelled);
        Assert.Equal(4, outcome.Exchanges.Count);
        Assert.All(outcome.Exchanges, e => Assert.Equal(302, e.Status));
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task ZeroMaxRedirectsStopsAtTheFirstRedirect()
    {
        await using var server = StartRedirectServer();
        var spec = Send.Get(server.Url("/302"));
        spec.Options.FollowRedirects = true;
        spec.Options.MaxRedirects = 0;

        var outcome = await Send.RunAsync(spec);

        Assert.Equal("Maximum (0) redirects followed", outcome.Error);
        Assert.Single(outcome.Exchanges);
    }

    [Fact]
    public async Task RedirectToAnUnsupportedSchemeStopsWithAnError()
    {
        await using var server = StartRedirectServer();
        var spec = Send.Get(server.Url("/ftp"));
        spec.Options.FollowRedirects = true;

        var outcome = await Send.RunAsync(spec);

        Assert.Contains("not supported", outcome.Error, StringComparison.Ordinal);
        Assert.Single(outcome.Exchanges);
    }

    [Fact]
    public void RelativeLocationsResolveAgainstTheCurrentUrl()
    {
        var hop = new HopRequest("GET", new Uri("http://h.test/a/b?q=1"), [], null, HttpVersionPreference.Default, false, []);

        var next = RedirectPolicy.Next(hop, hop.Uri, 302, "../c?x=2");

        Assert.Equal("http://h.test/c?x=2", next.Uri.AbsoluteUri);
        Assert.False(next.StripCredentials);
    }

    [Fact]
    public void SchemeOrPortChangeCountsAsAnotherHost()
    {
        var original = new Uri("http://h.test/");
        var hop = new HopRequest("GET", original, [new HarHeader("Authorization", "x")], null, HttpVersionPreference.Default, false, []);

        Assert.True(RedirectPolicy.Next(hop, original, 302, "https://h.test/").StripCredentials);
        Assert.True(RedirectPolicy.Next(hop, original, 302, "http://h.test:8080/").StripCredentials);
        Assert.False(RedirectPolicy.Next(hop, original, 302, "http://H.TEST/other").StripCredentials);
    }
}
