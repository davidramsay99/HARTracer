using HarLens.Core.Compare;
using HarLens.Core.Har;
using HarLens.Core.Model;
using HarLens.Core.Tests.Fixtures;

namespace HarLens.Core.Tests.Compare;

public sealed class CompareTests
{
    [Fact]
    public void Line_diff_pairs_modifications_and_keeps_context()
    {
        var rows = LineDiff.Compute("a\nb\nc\nd\ne", "a\nB\nc\ne\nf");
        Assert.Equal(
            [DiffKind.Equal, DiffKind.Modified, DiffKind.Equal, DiffKind.Deleted, DiffKind.Equal, DiffKind.Inserted],
            rows.Select(r => r.Kind));
        Assert.Equal((2, 2), (rows[1].LeftNumber!.Value, rows[1].RightNumber!.Value));
        Assert.Equal("f", rows[^1].Right);
    }

    [Fact]
    public void Identical_and_empty_inputs()
    {
        Assert.All(LineDiff.Compute("x\ny", "x\ny"), r => Assert.Equal(DiffKind.Equal, r.Kind));
        Assert.Equal([DiffKind.Inserted, DiffKind.Inserted], LineDiff.Compute("", "p\nq").Select(r => r.Kind));
        Assert.Empty(LineDiff.Compute("", ""));
    }

    [Fact]
    public void Large_diffs_fall_back_without_exploding()
    {
        var left = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => "L" + i));
        var right = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => "R" + i));
        var rows = LineDiff.Compute(left, right);
        Assert.Equal(20_000, rows.Count);
        Assert.All(rows, r => Assert.Equal(DiffKind.Modified, r.Kind));
    }

    [Fact]
    public void Random_edits_reconstruct_both_sides()
    {
        var rng = new Random(7);
        for (var trial = 0; trial < 50; trial++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => ((char)('a' + rng.Next(0, 5))).ToString()).ToList();
            var b = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => ((char)('a' + rng.Next(0, 5))).ToString()).ToList();
            var rows = LineDiff.Compute(a, b);
            Assert.Equal(a, rows.Where(r => r.Left is not null).Select(r => r.Left!));
            Assert.Equal(b, rows.Where(r => r.Right is not null).Select(r => r.Right!));
        }
    }

    [Fact]
    public void Headers_match_by_name_case_insensitively()
    {
        var rows = EntryComparison.DiffHeaders(
            [new HarHeader("Accept", "a"), new HarHeader("X-Old", "1"), new HarHeader("Cache-Control", "no-cache")],
            [new HarHeader("accept", "a"), new HarHeader("cache-control", "max-age=0"), new HarHeader("X-New", "2")]);
        Assert.Equal(DiffKind.Equal, rows.Single(r => r.Name == "Accept").Kind);
        Assert.Equal(DiffKind.Deleted, rows.Single(r => r.Name == "X-Old").Kind);
        Assert.Equal(DiffKind.Modified, rows.Single(r => r.Name == "Cache-Control").Kind);
        Assert.Equal(DiffKind.Inserted, rows.Single(r => r.Name == "X-New").Kind);
    }

    [Fact]
    public void Json_bodies_are_pretty_printed_before_diffing()
    {
        using var session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("chromium.har")).Document!);
        var cart = session.Entries.Single(e => e.Url.Contains("/api/cart", StringComparison.Ordinal));
        var checkout = session.Entries.Single(e => e.Method == "POST");
        var comparison = EntryComparison.Compare(cart, checkout);
        Assert.True(comparison.ResponseBodyPrettyPrinted);
        Assert.True(comparison.ResponseBody.Count > 3);
        Assert.False(comparison.Summary.Single(s => s.Field == "Method").Same);
        Assert.True(comparison.HasDifferences);
        Assert.False(EntryComparison.Compare(cart, cart).HasDifferences);
    }
}
