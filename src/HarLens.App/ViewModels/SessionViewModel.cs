using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarLens.App.Services;
using HarLens.Core.Composer;
using HarLens.Core.Curl;
using HarLens.Core.Export;
using HarLens.Core.Filtering;
using HarLens.Core.Har;
using HarLens.Core.Http;
using HarLens.Core.Saz;
using HarLens.Core.Sanitize;
using HarLens.Core.Settings;
using HarLens.Core.Stats;

namespace HarLens.App.ViewModels;

/// <summary>One tab: a loaded session with its list, filter bar and inspector (SPEC 5.5, 6).</summary>
public sealed partial class SessionViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _filterTimer;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _loadCts;
    private List<EntryRowViewModel> _allRows = [];
    private Dictionary<string, string> _pageLabels = [];
    private bool _suppressChipEvents;

    public SessionViewModel(MainViewModel main, string title)
    {
        _main = main;
        Title = title;
        Inspector = new InspectorViewModel(this);
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _ = ApplyFilterAsync();
        };
        foreach (var preset in AppServices.Current.Settings.FilterPresets)
        {
            Presets.Add(preset);
        }
    }

    public HarSession? Session { get; private set; }

    public InspectorViewModel Inspector { get; }

    public ObservableCollection<FilterPreset> Presets { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial double LoadProgress { get; set; }

    [ObservableProperty]
    public partial string? Banner { get; set; }

    [ObservableProperty]
    public partial bool BannerIsError { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<EntryRowViewModel> Rows { get; set; } = [];

    [ObservableProperty]
    public partial EntryRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string SelectionSummary { get; set; } = "";

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial string? FilterError { get; set; }

    [ObservableProperty]
    public partial string FilterStatus { get; set; } = "";

    [ObservableProperty]
    public partial FilterPreset? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial bool HasUnsavedChanges { get; set; }

    // Type chips (SPEC 6.4).
    [ObservableProperty]
    public partial bool ChipFetch { get; set; }

    [ObservableProperty]
    public partial bool ChipDoc { get; set; }

    [ObservableProperty]
    public partial bool ChipJs { get; set; }

    [ObservableProperty]
    public partial bool ChipCss { get; set; }

    [ObservableProperty]
    public partial bool ChipImg { get; set; }

    [ObservableProperty]
    public partial bool ChipFont { get; set; }

    [ObservableProperty]
    public partial bool ChipMedia { get; set; }

    [ObservableProperty]
    public partial bool ChipWs { get; set; }

    [ObservableProperty]
    public partial bool ChipOther { get; set; }

    // Status chips.
    [ObservableProperty]
    public partial bool Chip1xx { get; set; }

    [ObservableProperty]
    public partial bool Chip2xx { get; set; }

    [ObservableProperty]
    public partial bool Chip3xx { get; set; }

    [ObservableProperty]
    public partial bool Chip4xx { get; set; }

    [ObservableProperty]
    public partial bool Chip5xx { get; set; }

    [ObservableProperty]
    public partial bool ChipFailed { get; set; }

    public bool ChipAll
    {
        get => !(ChipFetch || ChipDoc || ChipJs || ChipCss || ChipImg || ChipFont || ChipMedia || ChipWs || ChipOther);
        set
        {
            if (!value)
            {
                return;
            }

            _suppressChipEvents = true;
            ChipFetch = ChipDoc = ChipJs = ChipCss = ChipImg = ChipFont = ChipMedia = ChipWs = ChipOther = false;
            _suppressChipEvents = false;
            OnChipChanged();
        }
    }

    public DateTimeOffset FirstStart { get; private set; }

    /// <summary>Width of the waterfall time axis in milliseconds (session span).</summary>
    public double AxisMilliseconds { get; private set; } = 1;

    public bool IsComposer => Session?.Kind == SessionKind.Composer;

    public IList? SelectedRows { get; private set; }

    public List<EntryRowViewModel> SelectedRowList => SelectedRows?.OfType<EntryRowViewModel>().ToList() ?? (SelectedRow is null ? [] : [SelectedRow]);

    public string? SortKey { get; private set; }

    public ListSortDirection SortDirection { get; private set; }

    public string PageGroupLabel(string? pageRef)
    {
        if (pageRef is null)
        {
            return "(no page)";
        }

        return _pageLabels.TryGetValue(pageRef, out var label) ? label : pageRef;
    }

    /// <summary>Loads a file off the UI thread with progress and cancel (SPEC 10: no UI-thread work over 50 ms).</summary>
    public async Task LoadAsync(string path)
    {
        IsLoading = true;
        Banner = null;
        _loadCts = new CancellationTokenSource();
        var progress = new Progress<HarLoadProgress>(p => LoadProgress = p.Fraction * 100);
        try
        {
            var isSaz = path.EndsWith(".saz", StringComparison.OrdinalIgnoreCase);
            var ct = _loadCts.Token;
            var result = await Task.Run(() => isSaz ? SazImporter.Import(path, ct) : HarReader.Load(path, progress, ct), ct);
            if (result.Document is null)
            {
                BannerIsError = true;
                Banner = result.FatalError ?? "The file could not be loaded.";
                AppLog.Warning($"Open failed for {path}: {result.FatalError}");
                return;
            }

            var session = HarSession.FromDocument(result.Document);
            var messages = new List<string>();
            if (result.Failure is { } failure)
            {
                messages.Add(failure.Truncated
                    ? $"The file is truncated: {failure.RecoveredEntries:N0} complete entries recovered; parsing stopped at byte offset {failure.ByteOffset:N0} (line {failure.Line}, column {failure.Column}, {failure.JsonPath})."
                    : $"Parse error at line {failure.Line}, column {failure.Column} ({failure.JsonPath}, byte offset {failure.ByteOffset:N0}): {failure.Message}. {failure.RecoveredEntries:N0} complete entries recovered.");
                BannerIsError = true;
            }

            messages.AddRange(result.Diagnostics.Where(d => d.Severity == HarDiagnosticSeverity.Warning).Select(d => d.Message));
            Banner = messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
            AppLog.Info($"Opened {path}: {session.Entries.Count} entries");
            Attach(session);
        }
        catch (OperationCanceledException)
        {
            Banner = "Loading was cancelled.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CancelLoad() => _loadCts?.Cancel();

    /// <summary>Shows a session that is already in memory (merged, composer, or loaded).</summary>
    public void Attach(HarSession session)
    {
        Session = session;
        RebuildRows();
        OnPropertyChanged(nameof(IsComposer));
        _ = ApplyFilterAsync();
    }

    /// <summary>Re-reads the session's entries (after composer sends) keeping the filter.</summary>
    public void RebuildRows()
    {
        if (Session is null)
        {
            return;
        }

        FirstStart = Session.FirstStart;
        var last = Session.LastEnd;
        AxisMilliseconds = Math.Max(1, (last - FirstStart).TotalMilliseconds);
        _pageLabels = Session.Pages.ToDictionary(
            p => p.Id,
            p => $"{(string.IsNullOrEmpty(p.Title) ? p.Id : p.Title)}    DOMContentLoaded {DisplayFormat.Duration(p.OnContentLoad)}    Load {DisplayFormat.Duration(p.OnLoad)}",
            StringComparer.Ordinal);
        _allRows = Session.Entries.Select(e => new EntryRowViewModel(e, this)).ToList();
    }

    public void SetSelectedRows(IList rows)
    {
        SelectedRows = rows;
        var list = rows.OfType<EntryRowViewModel>().Select(r => r.Entry).ToList();
        var summary = Core.Stats.SelectionSummary.Compute(list);
        SelectionSummary = summary.Count <= 1
            ? ""
            : $"{summary.Count:N0} selected    {DisplayFormat.HumanSize(summary.Bytes)}    span {DisplayFormat.Duration(summary.Span.TotalMilliseconds)}";
    }

    public void ScheduleFilter()
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    public void ClearFilter()
    {
        FilterText = "";
        ChipAll = true;
        _suppressChipEvents = true;
        Chip1xx = Chip2xx = Chip3xx = Chip4xx = Chip5xx = ChipFailed = false;
        _suppressChipEvents = false;
        _ = ApplyFilterAsync();
    }

    /// <summary>Filters the index off the UI thread, then re-applies the current sort (SPEC 6.4).</summary>
    public async Task ApplyFilterAsync()
    {
        if (Session is null)
        {
            return;
        }

        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        var expression = FilterExpression.Parse(FilterText);
        FilterError = expression.Errors.Count == 0 ? null : string.Join("; ", expression.Errors);
        var quick = BuildQuickFilter();
        var entries = Session.Entries;
        var rows = _allRows;
        var sortKey = SortKey;
        var direction = SortDirection;
        try
        {
            var result = await Task.Run(() =>
            {
                var matched = FilterEngine.Apply(entries, expression, quick, cts.Token);
                var list = new List<EntryRowViewModel>(matched.Count);
                foreach (var e in matched)
                {
                    list.Add(rows[e.Id - 1]);
                }

                Sort(list, sortKey, direction);
                return list;
            }, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var selected = SelectedRow;
            Rows = result;
            FilterStatus = result.Count == rows.Count ? $"{rows.Count:N0} entries" : $"{result.Count:N0} of {rows.Count:N0} entries";
            if (selected is not null && result.Contains(selected))
            {
                SelectedRow = selected;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void SortBy(string key, ListSortDirection direction)
    {
        SortKey = key;
        SortDirection = direction;
        var list = Rows.ToList();
        Sort(list, key, direction);
        Rows = list;
    }

    private static void Sort(List<EntryRowViewModel> list, string? key, ListSortDirection direction)
    {
        if (key is null)
        {
            return;
        }

        Comparison<EntryRowViewModel> compare = key switch
        {
            "Status" => (a, b) => a.Status.CompareTo(b.Status),
            "Method" => (a, b) => string.CompareOrdinal(a.Method, b.Method),
            "Protocol" => (a, b) => string.CompareOrdinal(a.Protocol, b.Protocol),
            "Host" => (a, b) => string.CompareOrdinal(a.Host, b.Host),
            "Path" => (a, b) => string.CompareOrdinal(a.Entry.Path, b.Entry.Path),
            "MimeType" => (a, b) => string.CompareOrdinal(a.MimeType, b.MimeType),
            "ResponseSize" => (a, b) => a.ResponseSize.CompareTo(b.ResponseSize),
            "TotalTime" or "Waterfall" => (a, b) => a.TotalTime.CompareTo(b.TotalTime),
            "Started" => (a, b) => a.Started.CompareTo(b.Started),
            "FileIndex" => (a, b) => a.FileIndex.CompareTo(b.FileIndex),
            "SourceTag" => (a, b) => string.CompareOrdinal(a.SourceTag, b.SourceTag),
            "Comment" => (a, b) => string.CompareOrdinal(a.Comment, b.Comment),
            _ when key.Contains(':', StringComparison.Ordinal) => (a, b) => string.CompareOrdinal(a[key], b[key]),
            _ => (a, b) => a.Id.CompareTo(b.Id),
        };
        Comparison<EntryRowViewModel> stable = (a, b) =>
        {
            var c = compare(a, b);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        };
        list.Sort(direction == ListSortDirection.Ascending ? stable : (a, b) => stable(b, a));
    }

    private QuickFilter BuildQuickFilter()
    {
        var q = new QuickFilter();
        void Cat(bool on, ResourceCategory c)
        {
            if (on)
            {
                q.Categories.Add(c);
            }
        }

        Cat(ChipFetch, ResourceCategory.Fetch);
        Cat(ChipDoc, ResourceCategory.Document);
        Cat(ChipJs, ResourceCategory.Script);
        Cat(ChipCss, ResourceCategory.Stylesheet);
        Cat(ChipImg, ResourceCategory.Image);
        Cat(ChipFont, ResourceCategory.Font);
        Cat(ChipMedia, ResourceCategory.Media);
        Cat(ChipWs, ResourceCategory.WebSocket);
        Cat(ChipOther, ResourceCategory.Other);
        void Status(bool on, int c)
        {
            if (on)
            {
                q.StatusClasses.Add(c);
            }
        }

        Status(Chip1xx, 1);
        Status(Chip2xx, 2);
        Status(Chip3xx, 3);
        Status(Chip4xx, 4);
        Status(Chip5xx, 5);
        Status(ChipFailed, QuickFilter.Failed);
        return q;
    }

    partial void OnFilterTextChanged(string value) => ScheduleFilter();

    partial void OnSelectedRowChanged(EntryRowViewModel? value) => _ = Inspector.ShowAsync(value);

    partial void OnSelectedPresetChanged(FilterPreset? value)
    {
        if (value is not null)
        {
            FilterText = value.Expression;
        }
    }

    partial void OnChipFetchChanged(bool value) => OnChipChanged();

    partial void OnChipDocChanged(bool value) => OnChipChanged();

    partial void OnChipJsChanged(bool value) => OnChipChanged();

    partial void OnChipCssChanged(bool value) => OnChipChanged();

    partial void OnChipImgChanged(bool value) => OnChipChanged();

    partial void OnChipFontChanged(bool value) => OnChipChanged();

    partial void OnChipMediaChanged(bool value) => OnChipChanged();

    partial void OnChipWsChanged(bool value) => OnChipChanged();

    partial void OnChipOtherChanged(bool value) => OnChipChanged();

    partial void OnChip1xxChanged(bool value) => OnChipChanged();

    partial void OnChip2xxChanged(bool value) => OnChipChanged();

    partial void OnChip3xxChanged(bool value) => OnChipChanged();

    partial void OnChip4xxChanged(bool value) => OnChipChanged();

    partial void OnChip5xxChanged(bool value) => OnChipChanged();

    partial void OnChipFailedChanged(bool value) => OnChipChanged();

    private void OnChipChanged()
    {
        if (_suppressChipEvents)
        {
            return;
        }

        OnPropertyChanged(nameof(ChipAll));
        ScheduleFilter();
    }

    public void OnAnnotationChanged() => HasUnsavedChanges = true;

    public void RefreshDisplay()
    {
        foreach (var row in _allRows)
        {
            row.Refresh();
        }

        Inspector.Refresh();
    }

    // ------------------------------------------------------------------ presets

    [RelayCommand]
    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return;
        }

        var name = Views.InputDialog.Ask("Save filter preset", "Preset name:", FilterText);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var settings = AppServices.Current.Settings;
        settings.FilterPresets.RemoveAll(p => p.Name == name);
        var preset = new FilterPreset { Name = name, Expression = FilterText };
        settings.FilterPresets.Add(preset);
        AppServices.Current.SaveSettings();
        _main.ReloadPresets();
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        AppServices.Current.Settings.FilterPresets.RemoveAll(p => p.Name == SelectedPreset.Name);
        AppServices.Current.SaveSettings();
        _main.ReloadPresets();
    }

    public void ReloadPresets()
    {
        Presets.Clear();
        foreach (var preset in AppServices.Current.Settings.FilterPresets)
        {
            Presets.Add(preset);
        }
    }

    // ------------------------------------------------------------------ per-entry actions (SPEC 6.8)

    private static bool Masking => AppServices.Current.Settings.MaskSecrets;

    private static string MaskHeaders(IEnumerable<Core.Model.HarHeader> headers) =>
        string.Join(Environment.NewLine, headers.Where(h => !h.Name.StartsWith(':')).Select(h => $"{h.Name}: {(Masking ? SecretMasker.MaskHeaderValue(h.Name, h.Value) : h.Value)}"));

    [RelayCommand]
    private void CopyUrl() => Ui.Copy(string.Join(Environment.NewLine, SelectedRowList.Select(r => Masking ? SecretMasker.MaskText(r.Entry.Url) : r.Entry.Url)));

    [RelayCommand]
    private void CopyRequestHeaders()
    {
        if (SelectedRow is { } row)
        {
            Ui.Copy(MaskHeaders(row.Entry.RequestHeaders));
        }
    }

    [RelayCommand]
    private void CopyResponseHeaders()
    {
        if (SelectedRow is { } row)
        {
            Ui.Copy(MaskHeaders(row.Entry.ResponseHeaders));
        }
    }

    [RelayCommand]
    private void CopyResponseBody()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var body = AppServices.Current.BodyCache.Get(row.Entry, BodySide.Response);
        if (body?.Text is { } text)
        {
            Ui.Copy(Masking ? SecretMasker.MaskText(text) : text);
        }
        else if (body is not null)
        {
            Ui.Info("The response body is binary. Use Save Response Body instead.");
        }
    }

    /// <summary>Copy as cURL (bash or cmd), PowerShell Invoke-WebRequest or raw HTTP (SPEC 6.8).</summary>
    [RelayCommand]
    private void CopyAs(string? format)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var exportFormat = Enum.TryParse<ExportFormat>(format, out var f) ? f : ExportFormat.CurlBash;
        Ui.Copy(ExportEntry(row.Entry, exportFormat));
    }

    public static string ExportEntry(HarEntry entry, ExportFormat format)
    {
        var spec = RequestFactory.FromEntry(entry, AppServices.Current.BodyCache.Get(entry, BodySide.Request));
        spec.Options.AutoDecompress = spec.HasHeader("Accept-Encoding");
        if (Masking)
        {
            foreach (var h in spec.Headers)
            {
                h.Value = SecretMasker.MaskHeaderValue(h.Name, h.Value);
            }

            spec.Url = SecretMasker.MaskText(spec.Url);
            if (spec.Body.Text is { } text)
            {
                spec.Body.Text = SecretMasker.MaskText(text);
            }
        }

        return RequestExporter.Export(spec, format).Text;
    }

    [RelayCommand]
    private void SaveResponseBody()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var body = AppServices.Current.BodyCache.Get(row.Entry, BodySide.Response);
        if (body is null || body.Bytes.Length == 0)
        {
            Ui.Info("This entry has no response body in the HAR.");
            return;
        }

        var pathAndQuery = UrlParts.Split(row.Entry.Url).PathAndQuery;
        var q = pathAndQuery.IndexOf('?');
        var name = ComposerStore.SafeFileName(System.IO.Path.GetFileName((q >= 0 ? pathAndQuery[..q] : pathAndQuery).TrimEnd('/')));
        if (name == "collection")
        {
            name = "response-body";
        }

        var path = Ui.SaveFile("Save response body", "All files (*.*)|*.*", name);
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(path, body.Bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error("Saving failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private void SendToComposer()
    {
        if (SelectedRow is { } row)
        {
            _main.OpenComposer(row.Entry);
        }
    }

    [RelayCommand]
    private void EditComment()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var text = Views.InputDialog.Ask("Comment", $"Comment for #{row.Id}:", row.Comment ?? "", multiline: true);
        if (text is not null)
        {
            foreach (var r in SelectedRowList)
            {
                r.Comment = text;
            }

            Inspector.Refresh();
        }
    }

    [RelayCommand]
    private void SetColor(string? color)
    {
        foreach (var r in SelectedRowList)
        {
            r.ColorMark = string.IsNullOrEmpty(color) || color == "none" ? null : color;
        }

        Inspector.Refresh();
    }

    [RelayCommand]
    private void Compare()
    {
        var rows = SelectedRowList;
        if (rows.Count != 2)
        {
            Ui.Info("Select exactly two entries to compare.");
            return;
        }

        _main.ShowCompare(rows[0].Entry, rows[1].Entry);
    }

    [RelayCommand]
    private void Statistics() => _main.ShowStatistics(this, SelectedRowList.Count > 1 ? SelectedRowList.Select(r => r.Entry).ToList() : null);

    // ------------------------------------------------------------------ per-session exports (SPEC 6.8)

    public string SuggestedName(string suffix, string extension)
    {
        var baseName = Path.GetFileNameWithoutExtension(Title.Replace(".har.gz", ".har", StringComparison.OrdinalIgnoreCase));
        return baseName + suffix + extension;
    }

    public async Task SaveHarAsync(IReadOnlyCollection<HarEntry>? entries, string suffix)
    {
        if (Session is null)
        {
            return;
        }

        var path = Ui.SaveFile(entries is null ? "Save HAR" : "Export selected entries", Ui.HarFilter, SuggestedName(suffix, ".har"));
        if (path is null)
        {
            return;
        }

        var session = Session;
        try
        {
            await Task.Run(() => HarWriter.Save(session, path, new HarWriteOptions { Entries = entries }));
            if (entries is null)
            {
                foreach (var e in session.Entries)
                {
                    e.AnnotationsDirty = false;
                }

                HasUnsavedChanges = false;
            }

            AppLog.Info($"Saved {path}");
            _main.StatusText = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error("Saving failed: " + ex.Message);
        }
    }

    public async Task ExportCsvAsync()
    {
        if (Session is null)
        {
            return;
        }

        var path = Ui.SaveFile("Export list as CSV", "CSV files (*.csv)|*.csv", SuggestedName("", ".csv"));
        if (path is null)
        {
            return;
        }

        var rows = Rows.Select(r => r.Entry).ToList();
        var columns = CsvExporter.DefaultColumns(FirstStart).ToList();
        foreach (var custom in AppServices.Current.Settings.CustomColumns)
        {
            var key = custom.Key;
            var lookup = _allRows;
            columns.Add(new CsvColumn(custom.Header, e => lookup[e.Id - 1][key] ?? ""));
        }

        try
        {
            await Task.Run(() => CsvExporter.Save(path, rows, columns));
            _main.StatusText = $"Exported {rows.Count:N0} rows to {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error("Export failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _filterCts?.Cancel();
        _loadCts?.Cancel();
        Inspector.Dispose();
        Session?.Dispose();
    }
}
