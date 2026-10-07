using System.Text;
using HarLens.Core.Composer;
using HarLens.Core.Engine;
using HarLens.Core.Har;
using HarLens.Core.Http;
using HarLens.Core.Model;
using HarLens.Core.Settings;
using HarLens.Core.Tests.Fixtures;

namespace HarLens.Core.Tests.Composer;

public sealed class ComposerTests
{
    [Fact]
    public void Entry_to_request_drops_pseudo_headers_content_length_and_redundant_host()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("chromium.har")).Document!);
        var spec = RequestFactory.FromEntry(session.Entries[0]);
        Assert.DoesNotContain(spec.Headers, h => h.Name.StartsWith(':'));
        Assert.Equal(HttpVersionPreference.Http2, spec.HttpVersion);
        Assert.Equal(session.Entries[0].Key, spec.OriginKey);

        using var firefox = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("firefox.har")).Document!);
        var post = RequestFactory.FromEntry(firefox.Entries[1]);
        Assert.DoesNotContain(post.Headers, h => h.Name is "Host" or "Content-Length");
        Assert.Equal(BodyMode.FormUrlEncoded, post.Body.Mode);
        Assert.Equal("q=alpha&limit=2", post.Body.Text);
    }

    [Fact]
    public void Binary_post_data_becomes_bytes()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("base64-bodies.har")).Document!);
        var spec = RequestFactory.FromEntry(session.Entries.Single(e => e.Method == "POST"));
        Assert.NotNull(spec.Body.Bytes);
        Assert.Equal(9, spec.Body.Bytes!.Length);
    }

    [Fact]
    public void Replay_guard_flags_captured_credentials_and_strips_them()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("secrets.har")).Document!);
        var origin = session.Entries[0];
        var spec = RequestFactory.FromEntry(origin);
        var findings = ReplayGuard.Inspect(spec, origin);
        Assert.Contains(findings, f => f.Name == "Authorization");
        Assert.Contains(findings, f => f.Name == "Cookie");
        Assert.Contains(findings, f => f is { Location: CredentialLocation.QueryParameter, Name: "access_token" });
        Assert.DoesNotContain(findings, f => f.Description.Contains("plantedBearerToken", StringComparison.Ordinal));

        var stripped = ReplayGuard.StripCredentials(spec, findings);
        Assert.Null(stripped.GetHeader("Authorization"));
        Assert.Null(stripped.GetHeader("Cookie"));
        Assert.Equal("https://api.contoso.test/me?view=full", stripped.Url);
        Assert.Empty(ReplayGuard.Inspect(stripped, origin));

        spec.SetHeader("Authorization", "Bearer something-new");
        Assert.DoesNotContain(ReplayGuard.Inspect(spec, origin), f => f.Name == "Authorization");
        Assert.Empty(ReplayGuard.Inspect(spec, origin: null));
    }

    [Fact]
    public void Exchanges_become_har_entries_in_the_composer_session()
    {
        using var composer = HarSession.CreateComposer();
        var exchange = new ExchangeRecord
        {
            StartedDateTime = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
            Method = "POST",
            Url = "https://api.example.test/items?x=1",
            HttpVersion = "HTTP/1.1",
            RequestHeaders = [new HarHeader("Host", "api.example.test"), new HarHeader("Content-Type", "application/json")],
            RequestBody = Encoding.UTF8.GetBytes("{\"a\":1}"),
            Status = 201,
            StatusText = "Created",
            ResponseHeaders = [new HarHeader("Content-Type", "application/json"), new HarHeader("Set-Cookie", "s=1; HttpOnly")],
            ResponseBody = Encoding.UTF8.GetBytes("{\"id\":7}"),
            ResponseBodyWireSize = 8,
            Timings = new ExchangeTimings { Dns = 1, Connect = 10, Ssl = 6, Send = 0.5, Wait = 20, Receive = 1, Total = 32.5 },
            RemoteAddress = "127.0.0.1",
            RemotePort = 443,
            Tls = new TlsDetails { Protocol = "Tls13", CipherSuite = "TLS_AES_256_GCM_SHA384", Subject = "CN=api.example.test", SubjectAlternativeNames = ["api.example.test"] },
            WireSent = Encoding.ASCII.GetBytes("POST /items?x=1 HTTP/1.1\r\nHost: api.example.test\r\n\r\n"),
            WireReceived = Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\n\r\n"),
        };

        var added = ExchangeConverter.Append(composer, [exchange], originKey: "1:100");
        var entry = Assert.Single(added);
        Assert.Equal(201, entry.Status);
        Assert.Equal("api.example.test", entry.Host);
        Assert.Equal(1, entry.Id);
        Assert.Equal("{\"id\":7}", BodyReader.Read(entry, BodySide.Response)!.Text);
        Assert.Equal("{\"a\":1}", BodyReader.Read(entry, BodySide.Request)!.Text);
        Assert.Equal(10, entry.Timings.Connect);
        var wire = ExchangeConverter.ReadWire(entry)!.Value;
        Assert.StartsWith("POST /items", Encoding.ASCII.GetString(wire.Sent), StringComparison.Ordinal);

        using var output = new MemoryStream();
        HarWriter.Write(composer, output, new HarWriteOptions());
        var reloaded = HarReader.LoadBytes(output.ToArray(), "composer.har");
        Assert.True(reloaded.Success);
        Assert.Equal("HarLens", reloaded.Document!.Log.CreatorName);
        Assert.Equal("1.2", reloaded.Document.Log.Version);
        reloaded.Document.Dispose();
    }

    [Fact]
    public void History_strips_credentials_by_default_and_collections_round_trip()
    {
        var root = Path.Combine(FixturePaths.Generated, "store-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, isPortable: false);
        paths.EnsureCreated();
        var store = new ComposerStore(paths);
        var request = new HttpRequestSpec { Method = "GET", Url = "https://x.test/" };
        request.Headers.Add(new HeaderEntry("Authorization", "Bearer secret-value"));
        request.Headers.Add(new HeaderEntry("Accept", "*/*"));
        store.AppendHistory(new HistoryItem { Sent = DateTimeOffset.Now, Request = request, Status = 200 }, keepCredentials: false);
        var history = store.LoadHistory();
        var item = Assert.Single(history);
        Assert.True(item.CredentialsRemoved);
        Assert.Equal("", item.Request.GetHeader("Authorization"));
        Assert.Equal("Bearer secret-value", request.GetHeader("Authorization"));
        store.ClearHistory();
        Assert.Empty(store.LoadHistory());

        var collection = new RequestCollection { Name = "Login: flows/v2" };
        collection.Requests.Add(new SavedRequest { Name = "token", Request = request });
        store.SaveCollection(collection);
        var loaded = Assert.Single(store.LoadCollections());
        Assert.Equal("Login: flows/v2", loaded.Name);
        Assert.Equal("Bearer secret-value", loaded.Requests[0].Request.GetHeader("Authorization"));
        Assert.True(File.Exists(Path.Combine(paths.CollectionsDirectory, "Login_ flows_v2.json")));
        Directory.Delete(root, recursive: true);
    }
}
