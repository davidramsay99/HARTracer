using System.Text.Json.Nodes;
using HarLens.Core.Har;
using HarLens.Core.Tests.Fixtures;

namespace HarLens.Core.Tests.Har;

public sealed class HarWriterTests
{
    public static IEnumerable<object[]> Fixtures() => FixturePaths.AllHarFixtures().Select(p => new object[] { Path.GetFileName(p) });

    /// <summary>Open, save, reopen gives a semantically identical HAR, vendor fields included.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Save_round_trip_is_semantically_identical(string fixture)
    {
        var path = FixturePaths.Har(fixture);
        var load = HarReader.Load(path);
        if (!load.Success)
        {
            return; // not-har.json and friends are covered elsewhere
        }

        using var session = HarSession.FromDocument(load.Document!);
        using var output = new MemoryStream();
        HarWriter.Write(session, output, new HarWriteOptions());

        var original = ParseFixture(path);
        var saved = JsonNode.Parse(output.ToArray());
        Assert.True(JsonNode.DeepEquals(original, saved), $"{fixture} changed on save");

        var reloaded = HarReader.LoadBytes(output.ToArray(), "saved");
        Assert.True(reloaded.Success, reloaded.FatalError);
        Assert.Equal(session.Entries.Count, reloaded.Document!.Entries.Count);
        reloaded.Document.Dispose();
    }

    [Fact]
    public void Annotations_are_written_to_comment_and_harlens_object()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("chromium.har")).Document!);
        var target = session.Entries[2];
        target.Comment = "slow fetch, check upstream";
        target.ColorMark = "red";
        var cleared = session.Entries.First(e => e.Comment is null && e != target);

        using var output = new MemoryStream();
        HarWriter.Write(session, output, new HarWriteOptions());
        var reloaded = HarReader.LoadBytes(output.ToArray(), "saved").Document!;
        var match = reloaded.Entries.Single(e => e.Url == target.Url && e.FileIndex == target.FileIndex);
        Assert.Equal("slow fetch, check upstream", match.Comment);
        Assert.Equal("red", match.ColorMark);
        Assert.Null(reloaded.Entries.Single(e => e.FileIndex == cleared.FileIndex).ColorMark);

        // Everything else in the annotated entry is unchanged.
        var originalEntry = JsonNode.Parse(target.Source.ReadBytes(target.Offset, target.Length))!.AsObject();
        var savedEntry = JsonNode.Parse(match.Source.ReadBytes(match.Offset, match.Length))!.AsObject();
        savedEntry.Remove("comment");
        savedEntry.Remove("_harlens");
        Assert.True(JsonNode.DeepEquals(originalEntry, savedEntry));
        reloaded.Dispose();
    }

    [Fact]
    public void Export_selected_writes_subset_with_referenced_pages_only()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("chromium.har")).Document!);
        var subset = session.Entries.Where(e => e.Status >= 400).ToList();
        using var output = new MemoryStream();
        HarWriter.Write(session, output, new HarWriteOptions { Entries = subset });
        var reloaded = HarReader.LoadBytes(output.ToArray(), "subset").Document!;
        Assert.Equal(subset.Count, reloaded.Entries.Count);
        Assert.Single(reloaded.Pages);
        reloaded.Dispose();
    }

    [Fact]
    public void Merge_tags_sources_and_keeps_page_ids_unique()
    {
        using var a = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("chromium.har")).Document!, "a.har");
        using var b = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("firefox.har")).Document!, "b.har");
        using var merged = HarSession.Merge([a, b], "merged");
        Assert.Equal(a.Entries.Count + b.Entries.Count, merged.Entries.Count);
        Assert.Contains(merged.Entries, e => e.SourceTag == "a.har");
        Assert.Contains(merged.Entries, e => e.SourceTag == "b.har");
        // Merged display numbers are independent of the original tabs.
        Assert.Equal(Enumerable.Range(1, merged.Entries.Count), merged.Entries.Select(e => e.Id));
        Assert.Equal(1, a.Entries[0].Id);

        using var output = new MemoryStream();
        HarWriter.Write(merged, output, new HarWriteOptions());
        var doc = JsonNode.Parse(output.ToArray())!;
        Assert.Equal("HarLens", (string?)doc["log"]!["creator"]!["name"]);
        var pageIds = doc["log"]!["pages"]!.AsArray().Select(p => (string?)p!["id"]).ToList();
        Assert.Equal(pageIds.Count, pageIds.Distinct().Count());
        var entries = doc["log"]!["entries"]!.AsArray();
        Assert.All(entries, e => Assert.NotNull(e!["_harlens"]!["source"]));
        // Firefox entries were remapped to the renamed page.
        Assert.Contains(entries, e => (string?)e!["pageref"] == "page_1_2");
    }

    [Fact]
    public void Refuses_to_overwrite_an_open_source()
    {
        var copy = Path.Combine(FixturePaths.Generated, $"overwrite-{Guid.NewGuid():N}.har");
        File.Copy(FixturePaths.Har("har11.har"), copy);
        using (var session = HarSession.FromDocument(HarReader.Load(copy).Document!))
        {
            Assert.Throws<IOException>(() => HarWriter.Save(session, copy));
        }

        File.Delete(copy);
    }

    private static JsonNode? ParseFixture(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            bytes = ms.ToArray();
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        return JsonNode.Parse(bytes);
    }
}
