using System.Text;
using Harborer.Core.Compare;
using Harborer.Core.Composer;
using Harborer.Core.Engine;
using Harborer.Core.Export;
using Harborer.Core.Filtering;
using Harborer.Core.Har;
using Harborer.Core.Sanitize;
using Harborer.Core.Search;
using Harborer.Core.Stats;

namespace Harborer.Isolation.Tests;

/// <summary>
/// After opening a HAR, filtering, searching and exporting, Harborer.Net.dll is not
/// loaded. The DLL is present beside this assembly (project reference), so the test proves it is not loaded rather
/// than merely absent. This is the only test in this assembly that touches the engine, and it runs in its own
/// test host process.
/// </summary>
public sealed class NetworkIsolationTests
{
    [Fact]
    public void Viewer_workflow_never_loads_the_network_assembly()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, RequestEngineLoader.AssemblyName + ".dll")));
        Assert.False(RequestEngineLoader.IsNetworkAssemblyLoaded, "Harborer.Net was loaded before the test started");

        var fixtures = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "har"), "*.har*").ToList();
        Assert.NotEmpty(fixtures);
        var output = Path.Combine(Path.GetTempPath(), "harborer-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            foreach (var path in fixtures)
            {
                var load = HarReader.Load(path);
                if (load.Document is null)
                {
                    continue;
                }

                using var session = HarSession.FromDocument(load.Document);

                // Filter.
                var quick = new QuickFilter();
                quick.StatusClasses.Add(2);
                var filtered = FilterEngine.Apply(session.Entries, FilterExpression.Parse("-status-code:5xx domain:*.test /api/"), quick);

                // Search.
                SearchEngine.Search(session.Entries, new SearchQuery { Text = "a", Scopes = SearchScope.All });

                // Inspect.
                foreach (var entry in session.Entries)
                {
                    BodyReader.Read(entry, BodySide.Request);
                    BodyReader.Read(entry, BodySide.Response);
                    using var detail = EntryDetail.Load(entry);
                    _ = detail.FormatRawJson();
                    _ = RequestFactory.FromEntry(entry);
                }

                if (session.Entries.Count >= 2)
                {
                    _ = EntryComparison.Compare(session.Entries[0], session.Entries[1]);
                }

                _ = SessionStatistics.Compute(session.Entries.ToList());

                // Export: HAR, selected entries, CSV, sanitized HAR.
                var name = Path.GetFileNameWithoutExtension(path);
                HarWriter.Save(session, Path.Combine(output, name + ".saved.har"));
                HarWriter.Save(session, Path.Combine(output, name + ".subset.har"), new HarWriteOptions { Entries = filtered });
                CsvExporter.Save(Path.Combine(output, name + ".csv"), session.Entries, CsvExporter.DefaultColumns(session.FirstStart));
                new Sanitizer(new SanitizeOptions()).ExportToFile(session, Path.Combine(output, name + ".sanitized.har"));
            }

            Assert.False(RequestEngineLoader.IsNetworkAssemblyLoaded,
                "Harborer.Net was loaded by the open/filter/search/export workflow: " +
                string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)));

            // The check is sensitive: loading the engine on demand does load the assembly.
            var engine = RequestEngineLoader.Load();
            Assert.NotNull(engine);
            Assert.True(RequestEngineLoader.IsNetworkAssemblyLoaded);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
