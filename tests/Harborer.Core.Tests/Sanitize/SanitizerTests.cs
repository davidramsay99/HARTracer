using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Harborer.Core.Har;
using Harborer.Core.Sanitize;
using Harborer.Core.Tests.Fixtures;

namespace Harborer.Core.Tests.Sanitize;

public sealed class SanitizerTests
{
    private static readonly string[] Planted = File.ReadAllLines(FixturePaths.Har("secrets.planted.txt")).Where(l => l.Length > 0).ToArray();

    private static SanitizeOptions FixtureOptions()
    {
        var options = new SanitizeOptions();
        options.HeaderNames.Add("X-Custom-Secret");
        options.UserPatterns.Add(@"ACCT-\d{8}");
        return options;
    }

    /// <summary>The sanitized export contains none of the planted secret strings (byte search).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sanitized_export_contains_no_planted_secret(bool hash)
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("secrets.har")).Document!);
        var options = FixtureOptions();
        options.HashValues = hash;
        var path = Path.Combine(FixturePaths.Generated, $"sanitized-{Guid.NewGuid():N}.har");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(FixturePaths.Har("secrets.har")));

        var result = new Sanitizer(options).ExportToFile(session, path);

        var output = File.ReadAllBytes(path);
        foreach (var secret in Planted)
        {
            Assert.True(output.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0, $"planted secret survived: {secret}");
        }

        Assert.True(result.TotalRedactions > 20);
        // The output is still a HAR with every entry.
        var reloaded = HarReader.LoadBytes(output, "sanitized");
        Assert.True(reloaded.Success, reloaded.FatalError);
        Assert.Equal(session.Entries.Count, reloaded.Document!.Entries.Count);
        reloaded.Document.Dispose();
        // The source file is untouched.
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(FixturePaths.Har("secrets.har"))));
        File.Delete(path);
    }

    [Fact]
    public void Preview_lists_redactions_masked_without_writing()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("secrets.har")).Document!);
        var preview = new Sanitizer(FixtureOptions()).Preview(session);
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.Header && r.Location.Contains("Authorization", StringComparison.Ordinal));
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.Cookie);
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.UrlParameter);
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.JsonProperty);
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.Jwt);
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.Bearer);
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.Pattern);
        Assert.Contains(preview.Redactions, r => r.Kind == RedactionKind.KnownValue);
        Assert.All(preview.Redactions, r => Assert.StartsWith("••••", r.MaskedOriginal, StringComparison.Ordinal));
        foreach (var secret in Planted)
        {
            Assert.DoesNotContain(preview.Redactions, r => r.MaskedOriginal.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Hashing_is_stable_so_equal_values_correlate()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("secrets.har")).Document!);
        var options = FixtureOptions();
        options.HashValues = true;
        options.Salt = "fixed-salt";
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        new Sanitizer(options).Export(session, a);
        new Sanitizer(options).Export(session, b);
        Assert.Equal(a.ToArray(), b.ToArray());

        var doc = JsonNode.Parse(a.ToArray())!;
        var first = doc["log"]!["entries"]!.AsArray()[0]!;
        var cookieHeader = first["request"]!["headers"]!.AsArray().First(h => (string?)h!["name"] == "Cookie")!["value"]!.GetValue<string>();
        var cookieArray = first["request"]!["cookies"]!.AsArray()[0]!["value"]!.GetValue<string>();
        Assert.StartsWith("[sha256:", cookieArray, StringComparison.Ordinal);
        Assert.Contains(cookieArray, cookieHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorization_keeps_scheme_and_cookies_keep_names()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("secrets.har")).Document!);
        using var output = new MemoryStream();
        new Sanitizer(FixtureOptions()).Export(session, output);
        var first = JsonNode.Parse(output.ToArray())!["log"]!["entries"]!.AsArray()[0]!;
        var headers = first["request"]!["headers"]!.AsArray().ToDictionary(h => (string)h!["name"]!, h => (string)h!["value"]!);
        Assert.Equal("Bearer [REDACTED]", headers["Authorization"]);
        Assert.Equal("session=[REDACTED]; theme=[REDACTED]", headers["Cookie"]);
        Assert.Equal("https://app.contoso.test/home?access_token=[REDACTED]", headers["Referer"]);
        Assert.Equal("https://api.contoso.test/me?access_token=[REDACTED]&view=full", (string?)first["request"]!["url"]);
    }

    [Fact]
    public void Drop_bodies_by_mime_and_all_response_bodies()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("secrets.har")).Document!);
        var options = FixtureOptions();
        options.DropBodyMimeTypes.Add("text/*");
        using var output = new MemoryStream();
        new Sanitizer(options).Export(session, output);
        var entries = JsonNode.Parse(output.ToArray())!["log"]!["entries"]!.AsArray();
        var html = entries[3]!["response"]!["content"]!.AsObject();
        Assert.False(html.ContainsKey("text"));
        Assert.True(entries[0]!["response"]!["content"]!.AsObject().ContainsKey("text"));

        options.DropAllResponseBodies = true;
        using var all = new MemoryStream();
        new Sanitizer(options).Export(session, all);
        Assert.All(JsonNode.Parse(all.ToArray())!["log"]!["entries"]!.AsArray(),
            e => Assert.False(e!["response"]!["content"]!.AsObject().ContainsKey("text")));
    }

    [Fact]
    public void Masker_keeps_last_four()
    {
        Assert.Equal("••••cdef", SecretMasker.Mask("0123456789abcdef"));
        Assert.Equal("Bearer ••••cdef", SecretMasker.MaskHeaderValue("Authorization", "Bearer 0123456789abcdef"));
        Assert.Equal("a=••••6789; b=••••", SecretMasker.MaskHeaderValue("Cookie", "a=0123456789; b=xy"));
        Assert.Equal("x?access_token=••••3456&y=1", SecretMasker.MaskText("x?access_token=abcdef123456&y=1"));
        Assert.Equal("plain", SecretMasker.MaskHeaderValue("Accept", "plain"));
    }
}
