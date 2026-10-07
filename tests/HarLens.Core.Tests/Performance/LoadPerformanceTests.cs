using System.Diagnostics;
using HarLens.Core.Har;
using HarLens.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace HarLens.Core.Tests.Performance;

[Collection("Large files")]
public sealed class LoadPerformanceTests(ITestOutputHelper output)
{
    [LargeFact]
    public void Hundred_megabyte_file_indexes_quickly()
    {
        var path = LargeHarGenerator.Ensure(entries: 26_000, bodyBytes: 1_200, label: "perf100mb");
        var size = new FileInfo(path).Length;
        var sw = Stopwatch.StartNew();
        var result = HarReader.Load(path);
        sw.Stop();
        Assert.True(result.Success, result.FatalError);
        output.WriteLine($"{size / 1e6:F1} MB, {result.Document!.Entries.Count} entries, load {sw.ElapsedMilliseconds} ms");
        result.Document.Dispose();
        Assert.InRange(size, 80_000_000, 140_000_000);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"load took {sw.Elapsed}");
    }
}

[Collection("Large files")]
public sealed class GigabyteLoadTests(ITestOutputHelper output)
{
    /// <summary>A 1 GB HAR opens with peak working set under 1.5 GB.</summary>
    [LargeFact]
    public void One_gigabyte_file_opens_within_memory_budget()
    {
        var path = LargeHarGenerator.Ensure(entries: 100_000, bodyBytes: 6_500, label: "gigabyte");
        var size = new FileInfo(path).Length;
        Assert.True(size >= 1_000_000_000, $"generated file is only {size} bytes");

        GC.Collect();
        var sw = Stopwatch.StartNew();
        var result = HarReader.Load(path);
        Assert.True(result.Success, result.FatalError);
        using var session = HarSession.FromDocument(result.Document!);
        sw.Stop();

        // Touch a few bodies through the bounded cache, as the inspector would.
        var cache = new BodyCache();
        foreach (var entry in session.Entries.Take(500))
        {
            cache.Get(entry, BodySide.Response);
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var peak = process.PeakWorkingSet64;
        var heap = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"{size / 1e9:F2} GB, {session.Entries.Count} entries, load {sw.ElapsedMilliseconds} ms, " +
                         $"managed heap {heap / 1e6:F0} MB, peak working set {peak / 1e6:F0} MB");
        Assert.Equal(100_000, session.Entries.Count);
        Assert.True(peak < 1_500_000_000L, $"peak working set {peak / 1e6:F0} MB");
    }
}
