using Harborer.Core.Filtering;
using Harborer.Core.Har;
using Harborer.Core.Tests.Fixtures;

namespace Harborer.Core.Tests.Filtering;

public sealed class FilterTests : IDisposable
{
    private readonly HarSession _session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("filter.har")).Document!);

    public void Dispose() => _session.Dispose();

    /// <summary>Every documented filter example returns the expected entry IDs from the filter fixture.</summary>
    [Theory]
    [InlineData("status-code:403", new[] { 3 })]
    [InlineData("status-code:5xx", new[] { 6, 7 })]
    [InlineData("method:POST", new[] { 3, 12 })]
    [InlineData("domain:*.contoso.com", new[] { 1, 2, 3, 5, 6, 9, 11, 12 })]
    [InlineData("scheme:https", new[] { 1, 2, 3, 4, 6, 7, 8, 9, 10, 12 })]
    [InlineData("mime-type:application/json", new[] { 2, 3 })]
    [InlineData("larger-than:100k", new[] { 2, 12 })]
    [InlineData("has-response-header:x-azure-ref", new[] { 1 })]
    [InlineData("has-request-header:authorization", new[] { 2 })]
    [InlineData("header:x-cache=TCP_MISS", new[] { 1 })]
    [InlineData("is:from-cache", new[] { 4 })]
    [InlineData("is:failed", new[] { 8 })]
    [InlineData("time-greater-than:2000", new[] { 2, 6 })]
    [InlineData(@"/v1\/items/", new[] { 2, 9 })]
    [InlineData("-mime-type:image/png", new[] { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11 })]
    public void Spec_examples(string expression, int[] expectedIds) => AssertIds(expression, expectedIds);

    [Theory]
    [InlineData("domain:*.contoso.com -status-code:5xx", new[] { 1, 2, 3, 5, 9, 11, 12 })]
    [InlineData("fabrikam", new[] { 4, 10 })]
    [InlineData("FABRIKAM -font", new[] { 4 })]
    [InlineData("method:post status-code:4xx", new[] { 3, 12 })]
    [InlineData("domain:contoso.com", new[] { 7 })]
    [InlineData("mime-type:image/*", new[] { 4, 12 })]
    [InlineData("header:x-cache=TCP_*", new[] { 1, 4 })]
    [InlineData("header:x-cache", new[] { 1, 4 })]
    [InlineData("scheme:wss", new[] { 11 })]
    [InlineData("resource-type:fetch", new[] { 2, 9, 12 })]
    [InlineData("time-less-than:150", new[] { 1, 3, 4, 5, 7, 8, 9, 10, 11, 12 })]
    [InlineData("/socket|font/", new[] { 10, 11 })]
    [InlineData("-/contoso/", new[] { 4, 8, 10 })]
    [InlineData("larger-than:150000", new[] { 2, 12 })]
    [InlineData("larger-than:150001", new[] { 12 })]
    [InlineData("unknownkey:value", new int[0])]
    public void Combinations_and_extensions(string expression, int[] expectedIds) => AssertIds(expression, expectedIds);

    [Fact]
    public void Empty_filter_returns_everything()
    {
        Assert.Equal(12, FilterEngine.Apply(_session.Entries, FilterExpression.Parse("   ")).Count);
    }

    [Fact]
    public void Invalid_terms_are_reported_and_ignored()
    {
        var expr = FilterExpression.Parse("status-code:abc /[unclosed/ larger-than:");
        Assert.Equal(3, expr.Errors.Count);
        Assert.Equal(12, FilterEngine.Apply(_session.Entries, expr).Count);
    }

    [Fact]
    public void Quick_chips_combine_or_within_group_and_with_text()
    {
        var quick = new QuickFilter();
        quick.Categories.Add(ResourceCategory.Image);
        Assert.Equal([4, 8], Ids(FilterEngine.Apply(_session.Entries, FilterExpression.Empty, quick)));
        quick.Categories.Add(ResourceCategory.Font);
        Assert.Equal([4, 8, 10], Ids(FilterEngine.Apply(_session.Entries, FilterExpression.Empty, quick)));
        quick.StatusClasses.Add(QuickFilter.Failed);
        Assert.Equal([8], Ids(FilterEngine.Apply(_session.Entries, FilterExpression.Empty, quick)));

        var status = new QuickFilter();
        status.StatusClasses.Add(4);
        status.StatusClasses.Add(5);
        Assert.Equal([3, 6, 7, 12], Ids(FilterEngine.Apply(_session.Entries, FilterExpression.Empty, status)));
        Assert.Equal([3, 12], Ids(FilterEngine.Apply(_session.Entries, FilterExpression.Parse("method:POST"), status)));
    }

    [Fact]
    public void Tokenizer_keeps_regex_with_spaces_and_quotes()
    {
        Assert.Equal(["/a b/", "-\"x y\"", "domain:z"], FilterExpression.Tokenize("/a b/  -\"x y\" domain:z"));
    }

    [Theory]
    [InlineData("100k", 100_000)]
    [InlineData("1.5M", 1_500_000)]
    [InlineData("2048", 2048)]
    [InlineData("10kb", 10_000)]
    public void Size_units(string text, long expected) => Assert.Equal(expected, FilterExpression.ParseSize(text));

    private void AssertIds(string expression, int[] expectedIds)
    {
        var parsed = FilterExpression.Parse(expression);
        Assert.Empty(parsed.Errors);
        Assert.Equal(expectedIds, Ids(FilterEngine.Apply(_session.Entries, parsed)));
    }

    private static int[] Ids(IEnumerable<HarEntry> entries) => entries.Select(e => e.Id).Order().ToArray();
}
