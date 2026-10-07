using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HarLens.Core.Har;
using HarLens.Core.Model;

namespace HarLens.Core.Search;

[Flags]
public enum SearchScope
{
    None = 0,
    Url = 1,
    RequestHeaders = 2,
    RequestBody = 4,
    ResponseHeaders = 8,
    ResponseBody = 16,
    All = Url | RequestHeaders | RequestBody | ResponseHeaders | ResponseBody,
}

public sealed class SearchQuery
{
    public string Text { get; init; } = "";

    public SearchScope Scopes { get; init; } = SearchScope.All;

    public bool CaseSensitive { get; init; }

    public bool Regex { get; init; }

    /// <summary>Stops after this many hits overall.</summary>
    public int MaxResults { get; init; } = 10_000;

    /// <summary>Hits kept per location (one URL, one header, one body).</summary>
    public int MaxHitsPerLocation { get; init; } = 100;
}

/// <summary>One match. <see cref="Index"/> and <see cref="Length"/> locate it in the searched text (URL, header value, or decoded body).</summary>
public sealed record SearchHit(
    HarEntry Entry,
    SearchScope Scope,
    string? HeaderName,
    int HeaderIndex,
    int Index,
    int Length,
    string Snippet,
    int SnippetMatchStart,
    int SnippetMatchLength)
{
    public string Location => Scope switch
    {
        SearchScope.Url => "URL",
        SearchScope.RequestHeaders => $"Request header {HeaderName}",
        SearchScope.ResponseHeaders => $"Response header {HeaderName}",
        SearchScope.RequestBody => "Request body",
        SearchScope.ResponseBody => "Response body",
        _ => Scope.ToString(),
    };
}

public sealed record SearchProgress(int EntriesSearched, int TotalEntries, int Hits)
{
    public double Fraction => TotalEntries == 0 ? 1 : (double)EntriesSearched / TotalEntries;
}

public sealed class SearchResult
{
    public List<SearchHit> Hits { get; } = [];

    public bool Truncated { get; set; }

    public string? Error { get; set; }

    public int EntriesWithHits => Hits.Select(h => h.Entry).Distinct().Count();
}

/// <summary>
/// Searches across all entries on background threads with cancellation and progress (SPEC 6.5).
/// Base64 bodies are decoded first when their MIME type is textual.
/// </summary>
public static class SearchEngine
{
    private const int SnippetContext = 40;

    public static Task<SearchResult> SearchAsync(IReadOnlyList<HarEntry> entries, SearchQuery query,
        IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Search(entries, query, progress, cancellationToken), cancellationToken);

    public static SearchResult Search(IReadOnlyList<HarEntry> entries, SearchQuery query,
        IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new SearchResult();
        if (string.IsNullOrEmpty(query.Text) || query.Scopes == SearchScope.None)
        {
            return result;
        }

        Matcher matcher;
        try
        {
            matcher = Matcher.Create(query);
        }
        catch (ArgumentException ex)
        {
            result.Error = "Invalid regular expression: " + ex.Message;
            return result;
        }

        const int chunk = 256;
        var chunkCount = (entries.Count + chunk - 1) / chunk;
        var perChunk = new List<SearchHit>?[chunkCount];
        var searched = 0;
        var totalHits = 0;
        var lastReport = 0;
        try
        {
            Parallel.For(0, chunkCount, new ParallelOptions { CancellationToken = cancellationToken }, (c, state) =>
            {
                var hits = new List<SearchHit>();
                var end = Math.Min(entries.Count, (c + 1) * chunk);
                for (var i = c * chunk; i < end; i++)
                {
                    if (Volatile.Read(ref totalHits) >= query.MaxResults)
                    {
                        state.Stop();
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var before = hits.Count;
                    SearchEntry(entries[i], query, matcher, hits);
                    Interlocked.Add(ref totalHits, hits.Count - before);
                }

                perChunk[c] = hits;
                var done = Interlocked.Add(ref searched, end - c * chunk);
                if (progress is not null && done - Volatile.Read(ref lastReport) >= 2048)
                {
                    Volatile.Write(ref lastReport, done);
                    progress.Report(new SearchProgress(done, entries.Count, Volatile.Read(ref totalHits)));
                }
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        foreach (var hits in perChunk)
        {
            if (hits is null)
            {
                continue;
            }

            foreach (var hit in hits)
            {
                if (result.Hits.Count >= query.MaxResults)
                {
                    result.Truncated = true;
                    break;
                }

                result.Hits.Add(hit);
            }
        }

        result.Truncated |= totalHits > query.MaxResults;
        progress?.Report(new SearchProgress(entries.Count, entries.Count, result.Hits.Count));
        return result;
    }

    /// <summary>Finds matches in a single text, for "find in current view" (Ctrl+F).</summary>
    public static List<(int Index, int Length)> FindAll(string text, SearchQuery query, int max = 10_000)
    {
        var matcher = Matcher.Create(query);
        return matcher.Find(text).Take(max).ToList();
    }

    private static void SearchEntry(HarEntry entry, SearchQuery query, Matcher matcher, List<SearchHit> hits)
    {
        if (query.Scopes.HasFlag(SearchScope.Url))
        {
            AddHits(entry, SearchScope.Url, null, -1, entry.Url, query, matcher, hits);
        }

        if (query.Scopes.HasFlag(SearchScope.RequestHeaders))
        {
            SearchHeaders(entry, SearchScope.RequestHeaders, entry.RequestHeaders, query, matcher, hits);
        }

        if (query.Scopes.HasFlag(SearchScope.ResponseHeaders))
        {
            SearchHeaders(entry, SearchScope.ResponseHeaders, entry.ResponseHeaders, query, matcher, hits);
        }

        if (query.Scopes.HasFlag(SearchScope.RequestBody) && entry.RequestBody.Exists)
        {
            AddHits(entry, SearchScope.RequestBody, null, -1, BodyReader.ReadSearchableText(entry, BodySide.Request), query, matcher, hits);
        }

        if (query.Scopes.HasFlag(SearchScope.ResponseBody) && entry.ResponseBody.Exists)
        {
            AddHits(entry, SearchScope.ResponseBody, null, -1, BodyReader.ReadSearchableText(entry, BodySide.Response), query, matcher, hits);
        }
    }

    private static void SearchHeaders(HarEntry entry, SearchScope scope, HarHeader[] headers, SearchQuery query, Matcher matcher, List<SearchHit> hits)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            // Search "Name: Value" so that a query can span the name and the value.
            var line = headers[i].Name + ": " + headers[i].Value;
            AddHits(entry, scope, headers[i].Name, i, line, query, matcher, hits);
        }
    }

    private static void AddHits(HarEntry entry, SearchScope scope, string? headerName, int headerIndex, string? text,
        SearchQuery query, Matcher matcher, List<SearchHit> hits)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var count = 0;
        foreach (var (index, length) in matcher.Find(text))
        {
            var start = Math.Max(0, index - SnippetContext);
            var end = Math.Min(text.Length, index + length + SnippetContext);
            var snippet = text[start..end].ReplaceLineEndings(" ");
            hits.Add(new SearchHit(entry, scope, headerName, headerIndex, index, length,
                (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : ""),
                index - start + (start > 0 ? 1 : 0), length));
            if (++count >= query.MaxHitsPerLocation)
            {
                break;
            }
        }
    }

    private abstract class Matcher
    {
        public static Matcher Create(SearchQuery query) => query.Regex
            ? new RegexMatcher(new Regex(query.Text,
                (query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2)))
            : new TextMatcher(query.Text, query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        public abstract IEnumerable<(int Index, int Length)> Find(string text);
    }

    private sealed class TextMatcher(string needle, StringComparison comparison) : Matcher
    {
        public override IEnumerable<(int Index, int Length)> Find(string text)
        {
            var index = 0;
            while (index <= text.Length - needle.Length)
            {
                var found = text.IndexOf(needle, index, comparison);
                if (found < 0)
                {
                    yield break;
                }

                yield return (found, needle.Length);
                index = found + Math.Max(1, needle.Length);
            }
        }
    }

    private sealed class RegexMatcher(Regex regex) : Matcher
    {
        public override IEnumerable<(int Index, int Length)> Find(string text)
        {
            var results = new List<(int, int)>();
            try
            {
                foreach (var m in regex.EnumerateMatches(text))
                {
                    results.Add((m.Index, m.Length));
                    if (results.Count > 100_000)
                    {
                        break;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }

            return results;
        }
    }
}
