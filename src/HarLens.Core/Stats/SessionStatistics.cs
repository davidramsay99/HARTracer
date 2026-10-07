using HarLens.Core.Har;
using HarLens.Core.Text;

namespace HarLens.Core.Stats;

public sealed record CountAndBytes(string Key, int Count, long Bytes, double TotalTime);

public sealed record HistogramBucket(double StartMs, double EndMs, int Count, int Failed);

/// <summary>Status-bar summary of a selection: count, total bytes, time span.</summary>
public sealed record SelectionSummary(int Count, long Bytes, TimeSpan Span)
{
    public static SelectionSummary Empty { get; } = new(0, 0, TimeSpan.Zero);

    public static SelectionSummary Compute(IEnumerable<HarEntry> entries)
    {
        var count = 0;
        long bytes = 0;
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;
        foreach (var e in entries)
        {
            count++;
            if (e.ResponseSize > 0)
            {
                bytes += e.ResponseSize;
            }

            if (e.StartedDateTime == DateTimeOffset.MinValue)
            {
                continue;
            }

            if (e.StartedDateTime < first)
            {
                first = e.StartedDateTime;
            }

            if (e.EndDateTime > last)
            {
                last = e.EndDateTime;
            }
        }

        return new SelectionSummary(count, bytes, last > first ? last - first : TimeSpan.Zero);
    }
}

/// <summary>The statistics panel for the selection or the whole session.</summary>
public sealed class SessionStatistics
{
    public int Count { get; private init; }

    public long BytesSent { get; private init; }

    public long BytesReceived { get; private init; }

    public DateTimeOffset First { get; private init; }

    public DateTimeOffset Last { get; private init; }

    public TimeSpan Span => Last > First ? Last - First : TimeSpan.Zero;

    /// <summary>Keys "1xx" to "5xx" and "Failed".</summary>
    public IReadOnlyList<CountAndBytes> ByStatusClass { get; private init; } = [];

    public IReadOnlyList<CountAndBytes> ByMimeCategory { get; private init; } = [];

    public IReadOnlyList<CountAndBytes> ByHost { get; private init; } = [];

    public IReadOnlyList<HarEntry> Slowest { get; private init; } = [];

    public IReadOnlyList<HarEntry> Largest { get; private init; } = [];

    public IReadOnlyList<HistogramBucket> Timeline { get; private init; } = [];

    public static SessionStatistics Compute(IReadOnlyCollection<HarEntry> entries, int buckets = 40)
    {
        long sent = 0, received = 0;
        var timed = entries.Where(e => e.StartedDateTime != DateTimeOffset.MinValue).ToList();
        var first = timed.Count == 0 ? DateTimeOffset.MinValue : timed.Min(e => e.StartedDateTime);
        var last = timed.Count == 0 ? DateTimeOffset.MinValue : timed.Max(e => e.EndDateTime);
        foreach (var e in entries)
        {
            sent += Math.Max(0, e.RequestSize);
            received += Math.Max(0, e.ResponseSize);
        }

        static string StatusKey(HarEntry e) => e.IsFailed ? "Failed" : e.StatusClass is >= 1 and <= 5 ? $"{e.StatusClass}xx" : "Other";

        return new SessionStatistics
        {
            Count = entries.Count,
            BytesSent = sent,
            BytesReceived = received,
            First = first,
            Last = last,
            ByStatusClass = Group(entries, StatusKey).OrderBy(g => g.Key == "Failed" ? "9" : g.Key, StringComparer.Ordinal).ToList(),
            ByMimeCategory = Group(entries, e => MimeTypes.Categorize(e.MimeType).ToString()).OrderByDescending(g => g.Count).ToList(),
            ByHost = Group(entries, e => e.HostDisplay.Length == 0 ? "(none)" : e.HostDisplay).OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList(),
            Slowest = entries.OrderByDescending(e => e.TotalTime).Take(10).ToList(),
            Largest = entries.OrderByDescending(e => e.ResponseSize).Take(10).ToList(),
            Timeline = Histogram(timed, first, last, buckets),
        };
    }

    private static IEnumerable<CountAndBytes> Group(IEnumerable<HarEntry> entries, Func<HarEntry, string> key) =>
        entries.GroupBy(key).Select(g => new CountAndBytes(g.Key, g.Count(), g.Sum(e => Math.Max(0, e.ResponseSize)), g.Sum(e => Math.Max(0, e.TotalTime))));

    private static List<HistogramBucket> Histogram(List<HarEntry> timed, DateTimeOffset first, DateTimeOffset last, int buckets)
    {
        var result = new List<HistogramBucket>();
        if (timed.Count == 0 || buckets <= 0)
        {
            return result;
        }

        var total = Math.Max(1, (last - first).TotalMilliseconds);
        var width = total / buckets;
        var counts = new int[buckets];
        var failed = new int[buckets];
        foreach (var e in timed)
        {
            var i = (int)Math.Min(buckets - 1, (e.StartedDateTime - first).TotalMilliseconds / width);
            counts[i]++;
            if (e.IsFailed)
            {
                failed[i]++;
            }
        }

        for (var i = 0; i < buckets; i++)
        {
            result.Add(new HistogramBucket(i * width, (i + 1) * width, counts[i], failed[i]));
        }

        return result;
    }
}
