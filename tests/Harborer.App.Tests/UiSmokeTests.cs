using System.Windows;
using Harborer.App.ViewModels;
using Harborer.App.Views;
using Harborer.Core.Engine;
using Harborer.Core.Settings;

namespace Harborer.App.Tests;

/// <summary>
/// Drives the real windows with fixtures: opening, selecting,
/// every body view, filtering, the tool windows, themes, and that Harborer.Net stays unloaded throughout.
/// </summary>
public sealed class UiSmokeTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "har", name);

    [Fact]
    public void Viewer_and_tools_work_without_loading_the_network_assembly() => UiHost.Run(async () =>
    {
        var main = new MainViewModel();
        var window = new MainWindow(main);
        Application.Current.MainWindow = window;
        window.Show();

        foreach (var fixture in new[] { "chromium.har", "firefox.har", "proxy-export.har", "base64-bodies.har", "compressed-bodies.har", "chromium.har.gz", "truncated.har" })
        {
            await main.OpenFileAsync(Fixture(fixture));
            var tab = main.SelectedSession!;
            Assert.NotNull(tab.Session);
            await UiHost.WaitUntil(() => tab.Rows.Count == tab.Session!.Entries.Count);

            foreach (var row in tab.Rows)
            {
                tab.SelectedRow = row;
                await UiHost.WaitUntil(() => tab.Inspector.Row == row && tab.Inspector.General.Count > 0);
                foreach (var view in Enum.GetValues<BodyViewKind>())
                {
                    tab.Inspector.Response.Body.SelectedView = view;
                    tab.Inspector.Request.Body.SelectedView = view;
                    await UiHost.Settle(10);
                }
            }
        }

        var chromium = main.Sessions[0];
        main.SelectedSession = chromium;
        chromium.FilterText = "status-code:5xx";
        await UiHost.WaitUntil(() => chromium.Rows.Count == 1);
        chromium.ClearFilter();
        await UiHost.WaitUntil(() => chromium.Rows.Count == chromium.Session!.Entries.Count);
        chromium.ChipImg = true;
        await UiHost.WaitUntil(() => chromium.Rows.Count < chromium.Session!.Entries.Count);
        chromium.ChipAll = true;

        // Display toggles and themes.
        main.MaskSecrets = true;
        main.TimeDisplay = TimeDisplay.Utc;
        main.SizeDisplay = SizeDisplay.Bytes;
        main.Layout = LayoutOrientation.Stacked;
        main.GroupByPage = true;
        await UiHost.Settle();
        main.Theme = ThemeChoice.Dark;
        await UiHost.Settle();
        main.Theme = ThemeChoice.Light;
        main.Layout = LayoutOrientation.SideBySide;
        main.GroupByPage = false;
        main.MaskSecrets = false;
        await UiHost.Settle();

        // Tool windows.
        var entries = chromium.Session!.Entries;
        main.ShowCompare(entries[2], entries[3]);
        main.ShowStatistics(chromium, null);
        new SearchWindow(new SearchViewModel(main)) { Owner = window }.Show();
        new SettingsWindow(main) { Owner = window }.Show();
        new SanitizeWindow(new SanitizeViewModel(chromium, chromium.Session, null)) { Owner = window }.Show();
        main.OpenComposer(entries[8]);
        await UiHost.Settle(500);

        // Merge two tabs.
        var merged = Core.Har.HarSession.Merge([main.Sessions[0].Session!, main.Sessions[1].Session!], "merged");
        var mergedTab = new SessionViewModel(main, "merged");
        mergedTab.Attach(merged);
        main.Sessions.Add(mergedTab);
        main.SelectedSession = mergedTab;
        await UiHost.WaitUntil(() => mergedTab.Rows.Count == merged.Entries.Count);

        Assert.True(main.OfflineMode);
        Assert.False(RequestEngineLoader.IsNetworkAssemblyLoaded,
            "Harborer.Net was loaded by the viewer: " + string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)));
    });
}
