using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarLens.App.Services;
using HarLens.App.Views;
using HarLens.Core.Har;
using HarLens.Core.Settings;

namespace HarLens.App.ViewModels;

/// <summary>The shell: tabs, file commands, display toggles, and the tool windows.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private ComposerWindow? _composerWindow;
    private SearchWindow? _searchWindow;

    public MainViewModel()
    {
        Instance = this;
        RecentFiles = new ObservableCollection<string>(Settings.RecentFiles);
    }

    public static MainViewModel? Instance { get; private set; }

    public AppSettings Settings => AppServices.Current.Settings;

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public ObservableCollection<string> RecentFiles { get; }

    [ObservableProperty]
    public partial SessionViewModel? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready. Offline Mode is on: nothing in HarLens uses the network.";

    public SessionViewModel? ComposerSession { get; private set; }

    public bool OfflineMode
    {
        get => Settings.OfflineMode;
        set
        {
            if (value == Settings.OfflineMode)
            {
                return;
            }

            if (!value && !Ui.Confirm(
                    "Turn Offline Mode OFF?\n\nThe Request Composer will then be able to send requests, and only when you press Send, " +
                    "only to the host you typed. Viewing HAR files never uses the network either way.",
                    "Offline Mode"))
            {
                OnPropertyChanged();
                return;
            }

            Settings.OfflineMode = value;
            AppServices.Current.SaveSettings();
            OnPropertyChanged();
            StatusText = value ? "Offline Mode is on: sending is disabled." : "Offline Mode is off: the composer can send requests when you press Send.";
            AppLog.Info("Offline Mode " + (value ? "ON" : "OFF"));
        }
    }

    public bool MaskSecrets
    {
        get => Settings.MaskSecrets;
        set
        {
            Settings.MaskSecrets = value;
            OnPropertyChanged();
            DisplayChanged();
        }
    }

    public ThemeChoice Theme
    {
        get => Settings.Theme;
        set
        {
            Settings.Theme = value;
            OnPropertyChanged();
            ThemeManager.Apply(value);
            AppServices.Current.SaveSettings();
        }
    }

    public TimeDisplay TimeDisplay
    {
        get => Settings.TimeDisplay;
        set
        {
            Settings.TimeDisplay = value;
            OnPropertyChanged();
            DisplayChanged();
        }
    }

    public SizeDisplay SizeDisplay
    {
        get => Settings.SizeDisplay;
        set
        {
            Settings.SizeDisplay = value;
            OnPropertyChanged();
            DisplayChanged();
        }
    }

    public LayoutOrientation Layout
    {
        get => Settings.Layout;
        set
        {
            Settings.Layout = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStackedLayout));
            AppServices.Current.SaveSettings();
        }
    }

    public bool IsStackedLayout
    {
        get => Layout == LayoutOrientation.Stacked;
        set => Layout = value ? LayoutOrientation.Stacked : LayoutOrientation.SideBySide;
    }

    public bool WordWrap
    {
        get => Settings.WordWrap;
        set
        {
            Settings.WordWrap = value;
            OnPropertyChanged();
        }
    }

    public bool GroupByPage
    {
        get => Settings.GroupByPage;
        set
        {
            Settings.GroupByPage = value;
            OnPropertyChanged();
        }
    }

    public double MonoFontSize
    {
        get => Settings.MonoFontSize;
        set
        {
            var clamped = Math.Clamp(value, 8, 32);
            Settings.MonoFontSize = clamped;
            Application.Current.Resources["MonoFontSize"] = clamped;
            OnPropertyChanged();
        }
    }

    private void DisplayChanged()
    {
        foreach (var s in Sessions)
        {
            s.RefreshDisplay();
        }

        AppServices.Current.NotifyDisplaySettingsChanged();
        AppServices.Current.SaveSettings();
    }

    // ------------------------------------------------------------------ files

    [RelayCommand]
    private async Task Open()
    {
        foreach (var path in Ui.OpenFiles())
        {
            await OpenFileAsync(path);
        }
    }

    [RelayCommand]
    private async Task OpenRecent(string? path)
    {
        if (path is null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            Ui.Error($"'{path}' no longer exists.");
            Settings.RecentFiles.Remove(path);
            RecentFiles.Remove(path);
            AppServices.Current.SaveSettings();
            return;
        }

        await OpenFileAsync(path);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        Settings.RecentFiles.Clear();
        RecentFiles.Clear();
        AppServices.Current.SaveSettings();
    }

    public async Task OpenFileAsync(string path, string? filter = null)
    {
        var tab = new SessionViewModel(this, Path.GetFileName(path));
        Sessions.Add(tab);
        SelectedSession = tab;
        StatusText = $"Opening {Path.GetFileName(path)}…";
        if (!string.IsNullOrEmpty(filter))
        {
            tab.FilterText = filter;
        }

        await tab.LoadAsync(path);
        Settings.AddRecentFile(Path.GetFullPath(path));
        RecentFiles.Clear();
        foreach (var r in Settings.RecentFiles)
        {
            RecentFiles.Add(r);
        }

        AppServices.Current.SaveSettings();
        StatusText = tab.Session is null ? $"Could not open {Path.GetFileName(path)}" : $"Opened {Path.GetFileName(path)}: {tab.Session.Entries.Count:N0} entries";
    }

    [RelayCommand]
    private void CloseTab(SessionViewModel? tab)
    {
        tab ??= SelectedSession;
        if (tab is null)
        {
            return;
        }

        if (tab.HasUnsavedChanges && !Ui.Confirm($"'{tab.Title}' has annotations that are not saved. Close it anyway?"))
        {
            return;
        }

        Sessions.Remove(tab);
        if (ReferenceEquals(tab, ComposerSession))
        {
            ComposerSession = null;
        }

        tab.Dispose();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (SelectedSession is { } s)
        {
            await s.SaveHarAsync(null, s.Session?.Kind == SessionKind.File ? ".annotated" : "");
        }
    }

    [RelayCommand]
    private async Task ExportSelected()
    {
        if (SelectedSession is { } s)
        {
            var entries = s.SelectedRowList.Select(r => r.Entry).ToList();
            if (entries.Count == 0)
            {
                Ui.Info("Select one or more entries first.");
                return;
            }

            await s.SaveHarAsync(entries, ".selected");
        }
    }

    [RelayCommand]
    private async Task ExportFiltered()
    {
        if (SelectedSession is { } s)
        {
            await s.SaveHarAsync(s.Rows.Select(r => r.Entry).ToList(), ".filtered");
        }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (SelectedSession is { } s)
        {
            await s.ExportCsvAsync();
        }
    }

    [RelayCommand]
    private void ExportSanitized()
    {
        if (SelectedSession is not { Session: { } session } tab)
        {
            return;
        }

        var selected = tab.SelectedRowList;
        var window = new SanitizeWindow(new SanitizeViewModel(tab, session, selected.Count > 1 ? selected.Select(r => r.Entry).ToList() : null))
        {
            Owner = Application.Current.MainWindow,
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private void Merge()
    {
        var candidates = Sessions.Where(s => s.Session is not null).ToList();
        if (candidates.Count < 2)
        {
            Ui.Info("Open at least two files to merge.");
            return;
        }

        var chosen = MergeDialog.Choose(candidates);
        if (chosen is null || chosen.Count < 2)
        {
            return;
        }

        var merged = HarSession.Merge(chosen.Select(c => c.Session!).ToList(), "Merged (" + string.Join(", ", chosen.Select(c => c.Title)) + ")");
        var tab = new SessionViewModel(this, merged.Name);
        tab.Attach(merged);
        Sessions.Add(tab);
        SelectedSession = tab;
    }

    public void ReloadPresets()
    {
        foreach (var s in Sessions)
        {
            s.ReloadPresets();
        }
    }

    // ------------------------------------------------------------------ tools

    [RelayCommand]
    private void FindAll()
    {
        if (_searchWindow is { IsLoaded: true })
        {
            _searchWindow.Activate();
            return;
        }

        _searchWindow = new SearchWindow(new SearchViewModel(this)) { Owner = Application.Current.MainWindow };
        _searchWindow.Show();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        new SettingsWindow(this) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private void ShowAbout() =>
        Ui.Info($"HarLens {HarWriter.CreatorVersion}\n\nLocal HAR viewer and request composer.\nState folder: {AppServices.Current.Paths.Root}" +
                (AppServices.Current.Paths.IsPortable ? " (portable mode)" : "") +
                "\n\nHarLens makes no network connections of its own. Only the Request Composer connects, only when you press Send with Offline Mode off.", "About HarLens");

    [RelayCommand]
    private void ToggleLayout() => Layout = Layout == LayoutOrientation.SideBySide ? LayoutOrientation.Stacked : LayoutOrientation.SideBySide;

    [RelayCommand]
    private void FontBigger() => MonoFontSize += 1;

    [RelayCommand]
    private void FontSmaller() => MonoFontSize -= 1;

    [RelayCommand]
    private void FontReset() => MonoFontSize = 12;

    [RelayCommand]
    private void SetTheme(string? theme) => Theme = Enum.TryParse<ThemeChoice>(theme, out var t) ? t : ThemeChoice.System;

    [RelayCommand]
    private void OpenComposerEmpty() => OpenComposer(null);

    public void OpenComposer(HarEntry? origin)
    {
        if (_composerWindow is not { IsLoaded: true })
        {
            _composerWindow = new ComposerWindow(new ComposerViewModel(this));
            _composerWindow.Show();
        }

        if (origin is not null)
        {
            ((ComposerViewModel)_composerWindow.DataContext).LoadFromEntry(origin);
        }

        _composerWindow.Activate();
    }

    /// <summary>The "Composer" session tab that every send appends to, created on first use.</summary>
    public SessionViewModel EnsureComposerSession()
    {
        if (ComposerSession is not null && Sessions.Contains(ComposerSession))
        {
            return ComposerSession;
        }

        var session = HarSession.CreateComposer();
        ComposerSession = new SessionViewModel(this, "Composer");
        ComposerSession.Attach(session);
        Sessions.Add(ComposerSession);
        return ComposerSession;
    }

    public HarEntry? FindEntryByKey(string? key)
    {
        if (key is null)
        {
            return null;
        }

        foreach (var s in Sessions)
        {
            if (s.Session?.Entries.FirstOrDefault(e => e.Key == key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public void ShowCompare(HarEntry left, HarEntry right) =>
        new CompareWindow(new CompareViewModel(left, right)) { Owner = Application.Current.MainWindow }.Show();

    public void DiffComposerEntryAgainstOrigin(HarEntry composerEntry)
    {
        using var detail = EntryDetail.Load(composerEntry);
        var origin = EntryDetail.Child(EntryDetail.Child(detail.Root, "_harlens"), "origin")?.GetString();
        if (FindEntryByKey(origin) is { } original)
        {
            ShowCompare(original, composerEntry);
        }
        else
        {
            Ui.Info("The original entry is not open any more.");
        }
    }

    public void ShowStatistics(SessionViewModel tab, IReadOnlyList<HarEntry>? selection) =>
        new StatisticsWindow(new StatisticsViewModel(tab, selection)) { Owner = Application.Current.MainWindow }.Show();

    [RelayCommand]
    private void Statistics()
    {
        if (SelectedSession is { Session: not null } tab)
        {
            var sel = tab.SelectedRowList;
            ShowStatistics(tab, sel.Count > 1 ? sel.Select(r => r.Entry).ToList() : null);
        }
    }

    public void SelectEntry(HarEntry entry)
    {
        foreach (var s in Sessions)
        {
            if (s.Session?.Entries.Contains(entry) != true)
            {
                continue;
            }

            SelectedSession = s;
            var row = s.Rows.FirstOrDefault(r => ReferenceEquals(r.Entry, entry));
            if (row is null)
            {
                s.ClearFilter();
                return;
            }

            s.SelectedRow = row;
            RowSelectionRequested?.Invoke(this, row);
            return;
        }
    }

    /// <summary>Asks the active session view to scroll a row into view.</summary>
    public event EventHandler<EntryRowViewModel>? RowSelectionRequested;
}
