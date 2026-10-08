using System.Runtime.CompilerServices;
using Harborer.Core.Har;

namespace Harborer.Core.Filtering;

/// <summary>
/// Quick-toggle chips. Within the type group and within the status group selections combine with OR;
/// the groups and the filter text combine with AND. An empty group means "All".
/// </summary>
public sealed class QuickFilter
{
    /// <summary>Status chip for failed or blocked requests.</summary>
    public const int Failed = 0;

    public HashSet<ResourceCategory> Categories { get; } = [];

    /// <summary>1 to 5 for 1xx to 5xx, and <see cref="Failed"/>.</summary>
    public HashSet<int> StatusClasses { get; } = [];

    public bool IsEmpty => Categories.Count == 0 && StatusClasses.Count == 0;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Matches(HarEntry entry)
    {
        if (Categories.Count > 0 && !Categories.Contains(ResourceCategories.Of(entry)))
        {
            return false;
        }

        if (StatusClasses.Count > 0)
        {
            var ok = (StatusClasses.Contains(Failed) && entry.IsFailed) ||
                     (!entry.IsFailed && StatusClasses.Contains(entry.StatusClass));
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public QuickFilter Clone()
    {
        var copy = new QuickFilter();
        copy.Categories.UnionWith(Categories);
        copy.StatusClasses.UnionWith(StatusClasses);
        return copy;
    }
}

/// <summary>Applies a filter to the index only (no body reads), in parallel for large sessions (under 100 ms for 200,000 entries).</summary>
public static class FilterEngine
{
    private const int ParallelThreshold = 20_000;

    public static List<HarEntry> Apply(IReadOnlyList<HarEntry> entries, FilterExpression expression, QuickFilter? quick = null,
        CancellationToken cancellationToken = default)
    {
        var hasQuick = quick is { IsEmpty: false };
        if (expression.IsEmpty && !hasQuick)
        {
            return entries.ToList();
        }

        var list = entries as List<HarEntry>;
        var array = list is null ? entries as HarEntry[] ?? entries.ToArray() : null;
        var count = entries.Count;
        HarEntry At(int i) => array is not null ? array[i] : list![i];
        var quickFilter = hasQuick ? quick : null;

        if (count < ParallelThreshold)
        {
            var result = new List<HarEntry>();
            for (var i = 0; i < count; i++)
            {
                if ((i & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var e = At(i);
                if ((quickFilter is null || quickFilter.Matches(e)) && expression.Matches(e))
                {
                    result.Add(e);
                }
            }

            return result;
        }

        // Range partitions keep per-row overhead to a field read and a predicate call.
        var flags = new bool[count];
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, count, 16_384),
            new ParallelOptions { CancellationToken = cancellationToken },
            [MethodImpl(MethodImplOptions.AggressiveOptimization)] (range) =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                {
                    var e = At(i);
                    flags[i] = (quickFilter is null || quickFilter.Matches(e)) && expression.Matches(e);
                }
            });

        var matched = new List<HarEntry>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++)
        {
            if (flags[i])
            {
                matched.Add(At(i));
            }
        }

        return matched;
    }
}
