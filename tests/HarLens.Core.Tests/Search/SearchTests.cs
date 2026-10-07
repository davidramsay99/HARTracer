using HarLens.Core.Har;
using HarLens.Core.Search;
using HarLens.Core.Tests.Fixtures;

namespace HarLens.Core.Tests.Search;

public sealed class SearchTests : IDisposable
{
    private readonly HarSession _session = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("chromium.har")).Document!);

    public void Dispose() => _session.Dispose();

    [Fact]
    public void Finds_text_across_scopes_with_snippets()
    {
        var result = SearchEngine.Search(_session.Entries, new SearchQuery { Text = "c-1001" });
        Assert.Contains(result.Hits, h => h.Scope == SearchScope.ResponseBody && h.Entry.Url.Contains("/api/cart", StringComparison.Ordinal));
        Assert.Contains(result.Hits, h => h.Scope == SearchScope.RequestBody && h.Entry.Method == "POST");
        var hit = result.Hits[0];
        Assert.Equal("c-1001", hit.Snippet.Substring(hit.SnippetMatchStart, hit.SnippetMatchLength));
    }

    [Fact]
    public void Scope_case_and_regex()
    {
        Assert.Empty(SearchEngine.Search(_session.Entries, new SearchQuery { Text = "C-1001", CaseSensitive = true }).Hits);
        var headers = SearchEngine.Search(_session.Entries, new SearchQuery { Text = "x-ms-request-id", Scopes = SearchScope.ResponseHeaders });
        Assert.Equal(2, headers.Hits.Count);
        Assert.All(headers.Hits, h => Assert.Equal("x-ms-request-id", h.HeaderName, ignoreCase: true));
        var regex = SearchEngine.Search(_session.Entries, new SearchQuery { Text = @"traceId"":""00-[0-9a-f]{32}", Regex = true });
        Assert.Single(regex.Hits);
        Assert.NotNull(SearchEngine.Search(_session.Entries, new SearchQuery { Text = "(", Regex = true }).Error);
    }

    [Fact]
    public void Base64_textual_bodies_are_decoded_before_searching()
    {
        using var doc = HarSession.FromDocument(HarReader.Load(FixturePaths.Har("base64-bodies.har")).Document!);
        var result = SearchEngine.Search(doc.Entries, new SearchQuery { Text = "\"encoded\":\"base64\"", Scopes = SearchScope.ResponseBody });
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task Cancellation_stops_the_search()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SearchEngine.SearchAsync(_session.Entries, new SearchQuery { Text = "a" }, null, cts.Token));
    }

    [Fact]
    public void Progress_reaches_completion_and_results_are_capped()
    {
        var reports = new List<SearchProgress>();
        var result = SearchEngine.Search(_session.Entries, new SearchQuery { Text = "e", MaxResults = 5 }, new SyncProgress<SearchProgress>(reports.Add));
        Assert.Equal(5, result.Hits.Count);
        Assert.True(result.Truncated);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
