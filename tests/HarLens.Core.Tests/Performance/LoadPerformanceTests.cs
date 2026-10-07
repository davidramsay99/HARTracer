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
