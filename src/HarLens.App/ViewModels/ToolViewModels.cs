using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarLens.App.Services;
using HarLens.Core.Compare;
using HarLens.Core.Har;
using HarLens.Core.Sanitize;
using HarLens.Core.Search;
using HarLens.Core.Settings;
using HarLens.Core.Stats;

namespace HarLens.App.ViewModels;

/// <summary>Search across all entries (Ctrl+Shift+F): background thread, progress, cancel.</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
    }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool InUrl { get; set; } = true;

    [ObservableProperty]
    public partial bool InRequestHeaders { get; set; } = true;

    [ObservableProperty]
    public partial bool InRequestBody { get; set; } = true;

    [ObservableProperty]
    public partial bool InResponseHeaders { get; set; } = true;

    [ObservableProperty]
    public partial bool InResponseBody { get; set; } = true;

    [ObservableProperty]
    public partial bool CaseSensitive { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial bool AllTabs { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial SearchHit? SelectedHit { get; set; }

    public ObservableCollection<SearchHit> Hits { get; } = [];

    [RelayCommand]
    private async Task Search()
    {
        _cts?.Cancel();
        if (string.IsNullOrEmpty(Query))
        {
            return;
        }

        var scopes = SearchScope.None;
        if (InUrl)
        {
            scopes |= SearchScope.Url;
        }

        if (InRequestHeaders)
        {
            scopes |= SearchScope.RequestHeaders;
        }

        if (InRequestBody)
        {
            scopes |= SearchScope.RequestBody;
        }

        if (InResponseHeaders)
        {
            scopes |= SearchScope.ResponseHeaders;
        }

        if (InResponseBody)
        {
            scopes |= SearchScope.ResponseBody;
        }

        var sessions = AllTabs ? _main.Sessions.Where(s => s.Session is not null).ToList() : _main.SelectedSession is { Session: not null } s ? [s] : [];
        var entries = sessions.SelectMany(t => t.Session!.Entries).ToList();
        var query = new SearchQuery { Text = Query, Scopes = scopes, CaseSensitive = CaseSensitive, Regex = UseRegex };
        var cts = new CancellationTokenSource();
        _cts = cts;
        Hits.Clear();
        IsSearching = true;
        Status = "Searching…";
        var progress = new Progress<SearchProgress>(p =>
        {
            Progress = p.Fraction * 100;
            Status = $"Searched {p.EntriesSearched:N0} of {p.TotalEntries:N0} entries, {p.Hits:N0} matches";
        });
        try
        {
            var result = await SearchEngine.SearchAsync(entries, query, progress, cts.Token);
            if (result.Error is not null)
            {
                Status = result.Error;
                return;
            }

            foreach (var hit in result.Hits)
            {
                Hits.Add(hit);
            }

            Status = $"{result.Hits.Count:N0} matches in {result.EntriesWithHits:N0} entries" + (result.Truncated ? " (result limit reached)" : "");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled.";
        }
        finally
        {
            IsSearching = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    partial void OnSelectedHitChanged(SearchHit? value)
    {
        if (value is null)
        {
            return;
        }

        _main.SelectEntry(value.Entry);
        HitNavigationRequested?.Invoke(this, value);
    }

    /// <summary>Raised so the inspector can switch tab and highlight the match.</summary>
    public static event EventHandler<SearchHit>? HitNavigationRequested;
}

public sealed record HeaderDiffRowView(string Name, string Left, string Right, DiffKind Kind);

/// <summary>Side-by-side compare of two entries, also used for original versus replay.</summary>
public sealed class CompareViewModel
{
    public CompareViewModel(HarEntry left, HarEntry right)
    {
        Comparison = EntryComparison.Compare(left, right, AppServices.Current.BodyCache);
        var mask = AppServices.Current.Settings.MaskSecrets;
        string M(string name, string? v) => v is null ? "" : mask ? SecretMasker.MaskHeaderValue(name, v) : v;
        RequestHeaders = Comparison.RequestHeaders.Select(h => new HeaderDiffRowView(h.Name, M(h.Name, h.Left), M(h.Name, h.Right), h.Kind)).ToList();
        ResponseHeaders = Comparison.ResponseHeaders.Select(h => new HeaderDiffRowView(h.Name, M(h.Name, h.Left), M(h.Name, h.Right), h.Kind)).ToList();
        Title = $"Compare #{left.Id} {left.Method} {Short(left.Url)}  ↔  #{right.Id} {right.Method} {Short(right.Url)}";
        LeftTitle = $"#{left.Id}  {(left.SourceTag ?? left.Source.DisplayName)}";
        RightTitle = $"#{right.Id}  {(right.SourceTag ?? right.Source.DisplayName)}";
    }

    public EntryComparison Comparison { get; }

    public string Title { get; }

    public string LeftTitle { get; }

    public string RightTitle { get; }

    public IReadOnlyList<FieldDiff> Summary => Comparison.Summary;

    public IReadOnlyList<HeaderDiffRowView> RequestHeaders { get; }

    public IReadOnlyList<HeaderDiffRowView> ResponseHeaders { get; }

    public IReadOnlyList<DiffLine> RequestBody => Comparison.RequestBody;

    public IReadOnlyList<DiffLine> ResponseBody => Comparison.ResponseBody;

    public string RequestBodyNote => Comparison.RequestBodyPrettyPrinted ? "JSON on both sides: pretty-printed before diffing." : "";

    public string ResponseBodyNote => Comparison.ResponseBodyPrettyPrinted ? "JSON on both sides: pretty-printed before diffing." : "";

    private static string Short(string url) => url.Length > 60 ? url[..60] + "…" : url;
}

public sealed record StatRow(string Key, int Count, string Bytes, string Share, double BarWidth);

public sealed record EntryStatRow(int Id, string Method, string Url, string Value, HarEntry Entry);

public sealed record HistogramBar(double Height, string ToolTip, double FailedHeight);

/// <summary>The statistics panel for a selection or the whole session.</summary>
public sealed class StatisticsViewModel
{
    private const double BarMax = 160;

    public StatisticsViewModel(SessionViewModel tab, IReadOnlyList<HarEntry>? selection)
    {
        var entries = selection ?? tab.Session!.Entries;
        var stats = SessionStatistics.Compute(entries.ToList());
        Title = selection is null ? $"Statistics: {tab.Title}" : $"Statistics: {selection.Count:N0} selected entries in {tab.Title}";
        Overview =
        [
            new("Requests", stats.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)),
            new("Bytes sent", DisplayFormat.HumanSize(stats.BytesSent)),
            new("Bytes received", DisplayFormat.HumanSize(stats.BytesReceived)),
            new("First request", DisplayFormat.Timestamp(stats.First)),
            new("Time span", DisplayFormat.Duration(stats.Span.TotalMilliseconds)),
        ];
        ByStatus = Rows(stats.ByStatusClass, stats.Count);
        ByMime = Rows(stats.ByMimeCategory, stats.Count);
        ByHost = Rows(stats.ByHost, stats.Count);
        Slowest = stats.Slowest.Select(e => new EntryStatRow(e.Id, e.Method, e.Url, DisplayFormat.Duration(e.TotalTime), e)).ToList();
        Largest = stats.Largest.Select(e => new EntryStatRow(e.Id, e.Method, e.Url, DisplayFormat.HumanSize(e.ResponseSize), e)).ToList();
        var peak = Math.Max(1, stats.Timeline.Count == 0 ? 1 : stats.Timeline.Max(b => b.Count));
        Timeline = stats.Timeline.Select(b => new HistogramBar(
            BarMax * b.Count / peak,
            $"+{DisplayFormat.Duration(b.StartMs)} to +{DisplayFormat.Duration(b.EndMs)}: {b.Count} requests{(b.Failed > 0 ? $", {b.Failed} failed" : "")}",
            BarMax * b.Failed / peak)).ToList();
        TimelineCaption = stats.Timeline.Count == 0 ? "" : $"Requests started per {DisplayFormat.Duration(stats.Timeline[0].EndMs)} interval";
    }

    public string Title { get; }

    public IReadOnlyList<Core.Stats.CountAndBytes> RawByHost { get; } = [];

    public IReadOnlyList<GeneralRow> Overview { get; }

    public IReadOnlyList<StatRow> ByStatus { get; }

    public IReadOnlyList<StatRow> ByMime { get; }

    public IReadOnlyList<StatRow> ByHost { get; }

    public IReadOnlyList<EntryStatRow> Slowest { get; }

    public IReadOnlyList<EntryStatRow> Largest { get; }

    public IReadOnlyList<HistogramBar> Timeline { get; }

    public string TimelineCaption { get; }

    private static List<StatRow> Rows(IEnumerable<CountAndBytes> groups, int total) =>
        groups.Select(g => new StatRow(g.Key, g.Count, DisplayFormat.HumanSize(g.Bytes),
            total == 0 ? "" : $"{100.0 * g.Count / total:0.#}%", total == 0 ? 0 : 200.0 * g.Count / total)).ToList();
}

/// <summary>Export sanitized HAR: options, a preview of every redaction, then write.</summary>
public sealed partial class SanitizeViewModel : ObservableObject
{
    private readonly SessionViewModel _tab;
    private readonly HarSession _session;
    private readonly IReadOnlyCollection<HarEntry>? _entries;
    private CancellationTokenSource? _cts;

    public SanitizeViewModel(SessionViewModel tab, HarSession session, IReadOnlyCollection<HarEntry>? entries)
    {
        _tab = tab;
        _session = session;
        _entries = entries;
        var settings = AppServices.Current.Settings;
        ExtraHeaders = string.Join(Environment.NewLine, settings.SanitizeExtraHeaders);
        ExtraParameters = string.Join(Environment.NewLine, settings.SanitizeExtraParameters);
        Patterns = string.Join(Environment.NewLine, settings.SanitizePatterns);
        Scope = entries is null ? $"All {session.Entries.Count:N0} entries" : $"{entries.Count:N0} selected entries";
    }

    public string Scope { get; }

    public string DefaultHeaders => string.Join(", ", SanitizeOptions.DefaultHeaderNames);

    public string DefaultParameters => string.Join(", ", SanitizeOptions.DefaultParameterNames);

    [ObservableProperty]
    public partial string ExtraHeaders { get; set; }

    [ObservableProperty]
    public partial string ExtraParameters { get; set; }

    [ObservableProperty]
    public partial string Patterns { get; set; }

    [ObservableProperty]
    public partial bool RedactJwt { get; set; } = true;

    [ObservableProperty]
    public partial bool RedactBearer { get; set; } = true;

    [ObservableProperty]
    public partial bool RedactJsonProperties { get; set; } = true;

    [ObservableProperty]
    public partial bool SweepKnownValues { get; set; } = true;

    [ObservableProperty]
    public partial bool DropAllResponseBodies { get; set; }

    [ObservableProperty]
    public partial string DropMimeTypes { get; set; } = "";

    [ObservableProperty]
    public partial bool HashValues { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Preview lists every redaction before anything is written.";

    [ObservableProperty]
    public partial bool PreviewDone { get; set; }

    public ObservableCollection<Redaction> Redactions { get; } = [];

    /// <summary>Raised when the export finished and the dialog can close.</summary>
    public event EventHandler? Completed;

    private SanitizeOptions BuildOptions()
    {
        static IEnumerable<string> Lines(string text) =>
            text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var o = new SanitizeOptions
        {
            RedactJwt = RedactJwt,
            RedactBearer = RedactBearer,
            RedactJsonProperties = RedactJsonProperties,
            RedactKnownValuesEverywhere = SweepKnownValues,
            DropAllResponseBodies = DropAllResponseBodies,
            HashValues = HashValues,
        };
        o.HeaderNames.UnionWith(Lines(ExtraHeaders));
        o.ParameterNames.UnionWith(Lines(ExtraParameters));
        o.UserPatterns.AddRange(Patterns.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        o.DropBodyMimeTypes.UnionWith(Lines(DropMimeTypes));
        var settings = AppServices.Current.Settings;
        settings.SanitizeExtraHeaders = Lines(ExtraHeaders).ToList();
        settings.SanitizeExtraParameters = Lines(ExtraParameters).ToList();
        settings.SanitizePatterns = Patterns.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        AppServices.Current.SaveSettings();
        return o;
    }

    [RelayCommand]
    private async Task Preview()
    {
        Sanitizer sanitizer;
        try
        {
            sanitizer = new Sanitizer(BuildOptions());
        }
        catch (ArgumentException ex)
        {
            Status = "Invalid pattern: " + ex.Message;
            return;
        }

        await Run(async ct =>
        {
            var result = await Task.Run(() => sanitizer.Preview(_session, _entries, new Progress<double>(p => Progress = p * 100), ct), ct);
            Redactions.Clear();
            foreach (var r in result.Redactions)
            {
                Redactions.Add(r);
            }

            PreviewDone = true;
            Status = $"{result.TotalRedactions:N0} redactions in {result.EntryCount:N0} entries ({result.DistinctSecretValues:N0} distinct secret values)" +
                     (result.TotalRedactions > result.Redactions.Count ? $"; the first {result.Redactions.Count:N0} are listed" : "") + ".";
        });
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = Ui.SaveFile("Export sanitized HAR", Ui.HarFilter, _tab.SuggestedName(".sanitized", ".har"));
        if (path is null)
        {
            return;
        }

        Sanitizer sanitizer;
        try
        {
            sanitizer = new Sanitizer(BuildOptions());
        }
        catch (ArgumentException ex)
        {
            Status = "Invalid pattern: " + ex.Message;
            return;
        }

        await Run(async ct =>
        {
            try
            {
                var result = await Task.Run(() => sanitizer.ExportToFile(_session, path, _entries, new Progress<double>(p => Progress = p * 100), ct), ct);
                AppLog.Info($"Sanitized export written to {path}");
                Status = $"Wrote {Path.GetFileName(path)} with {result.TotalRedactions:N0} redactions.";
                Completed?.Invoke(this, EventArgs.Empty);
            }
            catch (IOException ex)
            {
                Ui.Error(ex.Message);
            }
        });
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task Run(Func<CancellationToken, Task> action)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }
}
