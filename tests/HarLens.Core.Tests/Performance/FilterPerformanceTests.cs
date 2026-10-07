using System.Diagnostics;
using HarLens.Core.Filtering;
using HarLens.Core.Har;
using HarLens.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace HarLens.Core.Tests.Performance;

[Collection("Large files")]
public sealed class FilterPerformanceTests(ITestOutputHelper output)
{
    /// <summary>The filter applies to the index only and returns within 100 ms for 200,000 entries.</summary>
    [LargeFact]
    public void Two_hundred_thousand_entries_filter_under_100_ms()
    {
        var path = LargeHarGenerator.Ensure(entries: 200_000, bodyBytes: 120, label: "rows200k");
        var load = Stopwatch.StartNew();
        var result = HarReader.Load(path);
        using var session = HarSession.FromDocument(result.Document!);
        load.Stop();
        Assert.Equal(200_000, session.Entries.Count);
        output.WriteLine($"{new FileInfo(path).Length / 1e6:F0} MB loaded and sorted in {load.ElapsedMilliseconds} ms");

        string[] expressions =
        [
            "status-code:5xx", "domain:*.contoso.test -status-code:2xx", "header:x-cache=TCP_MISS", "larger-than:1k",
            "has-response-header:x-azure-ref method:POST", "/items\\/1[0-9]{3}\\b/", "search time-greater-than:500",
            "-mime-type:image/png scheme:https",
        ];
        var quick = new QuickFilter();
        quick.Categories.Add(ResourceCategory.Fetch);

        // Warm up JIT and the lazily computed URL parts and categories.
        FilterEngine.Apply(session.Entries, FilterExpression.Parse(expressions[0]), quick);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var expression in expressions)
        {
            var times = new List<double>();
            var count = 0;
            for (var run = 0; run < 3; run++)
            {
                var sw = Stopwatch.StartNew();
                count = FilterEngine.Apply(session.Entries, FilterExpression.Parse(expression), quick).Count;
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            output.WriteLine($"{expression,-50} {count,7} rows  first {times[0],7:F1} ms  best {times.Min(),7:F1} ms");
            Assert.True(times[0] < 100, $"'{expression}' took {times[0]:F1} ms");
        }
    }
}
