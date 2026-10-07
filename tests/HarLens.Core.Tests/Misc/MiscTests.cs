using System.Text;
using HarLens.Core.Export;
using HarLens.Core.Har;
using HarLens.Core.Http;
using HarLens.Core.Saz;
using HarLens.Core.Settings;
using HarLens.Core.Stats;
using HarLens.Core.Tests.Fixtures;
using HarLens.Core.Text;

namespace HarLens.Core.Tests.Misc;

public sealed class MiscTests
{
    [Fact]
    public void Statistics_break_down_the_session()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("filter.har")).Document!);
        var stats = SessionStatistics.Compute(session.Entries.ToList());
        Assert.Equal(12, stats.Count);
        Assert.Equal(1, stats.ByStatusClass.Single(s => s.Key == "Failed").Count);
        Assert.Equal(2, stats.ByStatusClass.Single(s => s.Key == "5xx").Count);
        Assert.Equal("api.contoso.com", stats.ByHost[0].Key);
        Assert.Equal(6, stats.Slowest[0].Id);
        Assert.Equal(12, stats.Largest[0].Id);
        Assert.Equal(40, stats.Timeline.Count);
        Assert.Equal(12, stats.Timeline.Sum(b => b.Count));

        var summary = SelectionSummary.Compute(session.Entries.Take(2));
        Assert.Equal(2, summary.Count);
        Assert.Equal(151_000, summary.Bytes);
        Assert.Equal(TimeSpan.FromMilliseconds(2600), summary.Span);
    }

    [Fact]
    public void Csv_quotes_and_neutralises_formulas()
    {
        Assert.Equal("plain", CsvExporter.Quote("plain", false));
        Assert.Equal("\"a,b\"", CsvExporter.Quote("a,b", false));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExporter.Quote("say \"hi\"", false));
        Assert.Equal("'=cmd()", CsvExporter.Quote("=cmd()", false));
        Assert.Equal("-1", CsvExporter.Quote("-1", true));

        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("chromium.har")).Document!);
        var writer = new StringWriter();
        CsvExporter.Write(writer, session.Entries, CsvExporter.DefaultColumns(session.FirstStart));
        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(12, lines.Length);
        Assert.StartsWith("#,Status,Method,Protocol,Host,Path", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("1,200,GET,HTTP/2,www.contoso-shop.test,/,text/html", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_keep_unknown_keys_and_default_offline()
    {
        var dir = Path.Combine(FixturePaths.Generated, "settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        Assert.True(SettingsStore.Load(path).OfflineMode);

        File.WriteAllText(path, """
            { "offlineMode": false, "theme": "Dark", "futureFeature": { "x": 1 }, // comment
              "recentFiles": ["a.har"], }
            """);
        var settings = SettingsStore.Load(path);
        Assert.False(settings.OfflineMode);
        Assert.Equal(ThemeChoice.Dark, settings.Theme);
        settings.AddRecentFile("b.har");
        SettingsStore.Save(path, settings);
        var text = File.ReadAllText(path);
        Assert.Contains("futureFeature", text, StringComparison.Ordinal);
        Assert.Equal(["b.har", "a.har"], SettingsStore.Load(path).RecentFiles);

        File.WriteAllText(path, "{ not json");
        Assert.True(SettingsStore.Load(path).OfflineMode);
        Assert.True(File.Exists(path + ".bad"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Portable_flag_redirects_state()
    {
        var exeDir = Path.Combine(FixturePaths.Generated, "portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exeDir);
        Assert.False(AppPaths.Resolve(exeDir).IsPortable);
        File.WriteAllText(Path.Combine(exeDir, AppPaths.PortableFlagName), "");
        var paths = AppPaths.Resolve(exeDir);
        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(exeDir, "data"), paths.Root);
        Directory.Delete(exeDir, recursive: true);
    }

    [Fact]
    public void Saz_import_converts_sessions()
    {
        var result = SazImporter.Import(Path.Combine(FixturePaths.Root, "saz", "sample.saz"));
        Assert.True(result.Success, result.FatalError);
        using var doc = result.Document!;
        Assert.Equal(4, doc.Entries.Count);
        var connect = doc.Entries[0];
        Assert.Equal("CONNECT", connect.Method);
        Assert.Equal("https://api.northwind.test:443", connect.Url);

        var json = doc.Entries[1];
        Assert.Equal("https://api.northwind.test/v1/orders?id=42", json.Url);
        Assert.Equal(200, json.Status);
        Assert.Equal("192.0.2.12", json.ServerIPAddress);
        Assert.Equal("slow order lookup", json.Comment);
        Assert.Equal(100, json.Timings.Wait, 3);
        Assert.Equal(35, json.Timings.Connect, 3);
        Assert.Equal("{\"saz\":\"chunked gzip json\",\"ok\":true}", BodyReader.Read(json, BodySide.Response)!.Text);

        var post = doc.Entries[2];
        Assert.Equal("http://legacy.northwind.test/login", post.Url);
        Assert.Equal("user=a&remember=yes", BodyReader.Read(post, BodySide.Request)!.Text);
        Assert.Equal("/home", post.RedirectUrl);

        Assert.True(doc.Entries[3].IsFailed);
    }

    [Fact]
    public void Formatters_and_jwt()
    {
        Assert.True(JsonPretty.TryFormat("{\"a\":[1,2],\"é\":\"ü\"}", out var json));
        Assert.Contains("\"é\": \"ü\"", json, StringComparison.Ordinal);
        Assert.False(JsonPretty.TryFormat("not json", out _));
        Assert.True(XmlPretty.TryFormat("<a><b>x</b></a>", out var xml));
        Assert.Contains("\n  <b>x</b>", xml, StringComparison.Ordinal);
        Assert.False(XmlPretty.TryFormat("<!DOCTYPE a SYSTEM \"http://example.test/x.dtd\"><a>", out _));
        var html = HtmlIndenter.Format("<html><body><div><p>Hi</p><br><script>if (a<b) {x()}</script></div></body></html>");
        Assert.Contains("\n      <p>", html, StringComparison.Ordinal);
        Assert.Contains("if (a<b) {x()}", html, StringComparison.Ordinal);

        Assert.Equal("00000000  48 69 21                                          |Hi!|", HexDump.FormatRow("Hi!"u8, 0));
        Assert.Equal(2, HexDump.RowCount(17));

        var token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ1c2VyIiwiZXhwIjoxNzkxMzYzNjAwfQ.c2ln";
        var jwt = Assert.Single(Jwt.FindAll($"Authorization: Bearer {token}", "header"));
        Assert.Contains("\"alg\": \"HS256\"", jwt.HeaderJson, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791363600), jwt.Expires);
    }

    [Fact]
    public void Multipart_bodies_split_into_parts()
    {
        var body = Encoding.UTF8.GetBytes("--XyZ\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nhello\r\n" +
                                          "--XyZ\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.png\"\r\nContent-Type: image/png\r\n\r\n\u0001\u0002\r\n--XyZ--\r\n");
        var parts = MultipartParser.Parse(body, "multipart/form-data; boundary=XyZ");
        Assert.Equal(2, parts.Count);
        Assert.Equal("title", parts[0].Name);
        Assert.Equal("hello", parts[0].Preview);
        Assert.Equal("a.png", parts[1].FileName);
        Assert.Equal("image/png", parts[1].ContentType);
        Assert.Equal("[2 bytes]", parts[1].Preview);
        Assert.Equal(2, MultipartParser.Parse(body, null).Count);
    }

    [Fact]
    public void Url_and_form_helpers()
    {
        var parts = UrlParts.Split("https://user:pw@Example.COM:8443/a/b?x=1&y=two%20words#frag");
        Assert.Equal(("https", "example.com", 8443, "/a/b?x=1&y=two%20words"), (parts.Scheme, parts.Host, parts.Port, parts.PathAndQuery));
        Assert.Equal("x=1&y=two%20words", parts.Query);
        Assert.Equal("data", UrlParts.Split("data:text/plain,hi").Scheme);
        Assert.Equal("[::1]", UrlParts.Split("http://[::1]:8080/").Host);
        Assert.Equal(["x", "y"], FormUrlEncoding.ParseQuery("https://a/?x=1&y=a+b").Select(p => p.Name));
        Assert.Equal("a b", FormUrlEncoding.ParseQuery("https://a/?x=1&y=a+b")[1].Value);
        Assert.Equal("https://a/p?z=9#f", FormUrlEncoding.WithQuery("https://a/p?x=1#f", "z=9"));
        Assert.Equal("a+b%26c=d", FormUrlEncoding.Serialize([new("a b&c", "d")]).Replace("%3D", "=", StringComparison.Ordinal).Split('=')[0] + "=d");
    }
}
