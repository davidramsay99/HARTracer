using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Harborer.App.Services;
using Harborer.App.ViewModels;
using Harborer.Core.Settings;

namespace Harborer.App.Views;

/// <summary>
/// The session tab. Columns are built in code so that they can be hidden, reordered, persisted, and extended with
/// custom header or vendor-field columns. Sorting is done by the view model on the filtered list rather
/// than through a CollectionView, which keeps 200,000-row sessions responsive.
/// </summary>
public partial class SessionView : UserControl
{
    private static WeakReference<SessionView>? s_active;
    private readonly ColumnSpec[] _builtIn =
    [
        new("Id", "#", "Id", 56, Align: TextAlignment.Right),
        new("Status", "Status", "StatusDisplay", 58),
        new("Method", "Method", "Method", 66),
        new("Protocol", "Protocol", "Protocol", 72),
        new("Host", "Host", "Host", 190),
        new("Path", "Path", "Path", 320),
        new("MimeType", "MIME type", "MimeType", 140),
        new("ResponseSize", "Response size", "ResponseSizeText", 92, Align: TextAlignment.Right),
        new("TotalTime", "Total time", "TotalTimeText", 82, Align: TextAlignment.Right),
        new("Started", "Started", "StartedText", 96, Align: TextAlignment.Right),
        new("Waterfall", "Waterfall", null, 220),
        new("FileIndex", "Original #", "FileIndex", 70, Visible: false, Align: TextAlignment.Right),
        new("SourceTag", "Source", "SourceTag", 140, Visible: false),
        new("Comment", "Comment", "Comment", 180, Visible: false),
    ];

    private SessionViewModel? _vm;
    private bool _columnsBuilt;

    public SessionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            s_active = new WeakReference<SessionView>(this);
            ApplyLayout();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                s_active = new WeakReference<SessionView>(this);
            }
        };
        if (MainViewModel.Instance is { } main)
        {
            main.PropertyChanged += OnMainPropertyChanged;
            main.RowSelectionRequested += (_, row) =>
            {
                if (_vm?.Rows.Contains(row) == true)
                {
                    List.ScrollIntoView(row);
                }
            };
        }

        AppServices.Current.DisplaySettingsChanged += (_, _) => List.Items.Refresh();
        SearchViewModel.HitNavigationRequested += (_, hit) => InspectorPane.ShowSearchHit(hit);
    }

    private sealed record ColumnSpec(string Key, string Header, string? Path, double Width, bool Visible = true, TextAlignment Align = TextAlignment.Left);

    public static void FocusFilterOfActive()
    {
        if (s_active?.TryGetTarget(out var view) == true)
        {
            view.FocusFilter();
        }
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as SessionViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnVmPropertyChanged;
        if (!_columnsBuilt)
        {
            BuildColumns();
            _columnsBuilt = true;
        }

        UpdateItemsSource();
        ApplyLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.Rows))
        {
            UpdateItemsSource();
        }
        else if (e.PropertyName == nameof(SessionViewModel.IsComposer) && _vm?.Session?.Kind == Core.Har.SessionKind.Merged)
        {
            SetColumnVisible("SourceTag", true);
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Layout):
                ApplyLayout();
                break;
            case nameof(MainViewModel.GroupByPage):
                UpdateItemsSource();
                break;
        }
    }

    /// <summary>Plain list for speed; a grouped view only when "Group by page" is on and the HAR has pages.</summary>
    private void UpdateItemsSource()
    {
        if (_vm is null)
        {
            return;
        }

        var rows = _vm.Rows;
        if (AppServices.Current.Settings.GroupByPage && _vm.Session?.Pages.Count > 0)
        {
            var view = new ListCollectionView(rows.ToList());
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EntryRowViewModel.PageGroup)));
            List.ItemsSource = view;
        }
        else
        {
            List.ItemsSource = rows;
        }

        if (_vm.Session?.Kind == Core.Har.SessionKind.Merged)
        {
            SetColumnVisible("SourceTag", true);
        }
    }

    // ------------------------------------------------------------------ columns

    private void BuildColumns()
    {
        var settings = AppServices.Current.Settings;
        var saved = settings.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var columns = new List<(DataGridColumn Column, int Order)>();
        var order = 0;
        foreach (var spec in _builtIn)
        {
            var column = spec.Key == "Waterfall"
                ? (DataGridColumn)new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["WaterfallTemplate"] }
                : TextColumn(spec.Path!, spec.Align);
            Configure(column, spec.Key, spec.Header, spec.Width, spec.Visible, saved);
            columns.Add((column, saved.TryGetValue(spec.Key, out var s) ? s.DisplayIndex : order));
            order++;
        }

        foreach (var custom in settings.CustomColumns)
        {
            var column = CustomColumn(custom.Key);
            Configure(column, custom.Key, custom.Header, 150, true, saved);
            columns.Add((column, saved.TryGetValue(custom.Key, out var s) ? s.DisplayIndex : order));
            order++;
        }

        List.Columns.Clear();
        foreach (var (column, _) in columns.OrderBy(c => c.Order))
        {
            List.Columns.Add(column);
        }

        List.ColumnHeaderStyle = HeaderStyle();
    }

    private static DataGridTextColumn TextColumn(string path, TextAlignment align)
    {
        var column = new DataGridTextColumn { Binding = new Binding(path) { Mode = BindingMode.OneWay } };
        if (align == TextAlignment.Right)
        {
            column.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
        }

        return column;
    }

    private static DataGridTextColumn CustomColumn(string key) => new()
    {
        Binding = new Binding { Mode = BindingMode.OneWay, Converter = CustomValueConverter.Instance, ConverterParameter = key },
    };

    private void Configure(DataGridColumn column, string key, string header, double width, bool visible, Dictionary<string, ColumnSetting> saved)
    {
        column.Header = header;
        column.SortMemberPath = key;
        column.CanUserSort = true;
        if (saved.TryGetValue(key, out var s))
        {
            column.Width = s.Width > 10 ? s.Width : width;
            column.Visibility = s.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            column.Width = width;
            column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Persist width changes.
        DependencyPropertyDescriptor.FromProperty(DataGridColumn.ActualWidthProperty, typeof(DataGridColumn))
            .AddValueChanged(column, (_, _) => ScheduleColumnSave());
    }

    private Style HeaderStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), TryFindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)) as Style);
        style.Setters.Add(new EventSetter(ContextMenuOpeningEvent, new ContextMenuEventHandler(OnHeaderContextMenuOpening)));
        style.Setters.Add(new Setter(ContextMenuProperty, new ContextMenu()));
        return style;
    }

    private void OnHeaderContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement header || header.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();
        foreach (var column in List.Columns.OrderBy(c => c.DisplayIndex))
        {
            var item = new MenuItem { Header = column.Header, IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            var target = column;
            item.Click += (_, _) =>
            {
                target.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveColumns();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add Custom Column…" };
        add.Click += (_, _) => AddCustomColumn();
        menu.Items.Add(add);
        var customs = AppServices.Current.Settings.CustomColumns.ToList();
        if (customs.Count > 0)
        {
            var remove = new MenuItem { Header = "Remove Custom Column" };
            foreach (var custom in customs)
            {
                var removeItem = new MenuItem { Header = custom.Header };
                removeItem.Click += (_, _) => RemoveCustomColumn(custom);
                remove.Items.Add(removeItem);
            }

            menu.Items.Add(remove);
        }

        var reset = new MenuItem { Header = "Reset Columns" };
        reset.Click += (_, _) =>
        {
            AppServices.Current.Settings.Columns.Clear();
            AppServices.Current.SaveSettings();
            BuildColumns();
        };
        menu.Items.Add(reset);
    }

    private void AddCustomColumn()
    {
        var custom = CustomColumnDialog.Ask();
        if (custom is null)
        {
            return;
        }

        var settings = AppServices.Current.Settings;
        if (settings.CustomColumns.Any(c => c.Key == custom.Key))
        {
            return;
        }

        settings.CustomColumns.Add(custom);
        AppServices.Current.SaveSettings();
        var column = CustomColumn(custom.Key);
        Configure(column, custom.Key, custom.Header, 150, true, []);
        List.Columns.Add(column);
        SaveColumns();
    }

    private void RemoveCustomColumn(CustomColumn custom)
    {
        AppServices.Current.Settings.CustomColumns.RemoveAll(c => c.Key == custom.Key);
        var column = List.Columns.FirstOrDefault(c => c.SortMemberPath == custom.Key);
        if (column is not null)
        {
            List.Columns.Remove(column);
        }

        SaveColumns();
    }

    private void SetColumnVisible(string key, bool visible)
    {
        if (List.Columns.FirstOrDefault(c => c.SortMemberPath == key) is { } column)
        {
            column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    private void ScheduleColumnSave()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            SaveColumns();
        }, Dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnColumnLayoutChanged(object? sender, DataGridColumnEventArgs e) => SaveColumns();

    private void SaveColumns()
    {
        if (!_columnsBuilt)
        {
            return;
        }

        var settings = AppServices.Current.Settings;
        settings.Columns = List.Columns.Select(c => new ColumnSetting
        {
            Key = c.SortMemberPath,
            DisplayIndex = c.DisplayIndex,
            Width = c.ActualWidth,
            Visible = c.Visibility == Visibility.Visible,
        }).ToList();
        AppServices.Current.SaveSettings();
    }

    // ------------------------------------------------------------------ sorting, selection, keys

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_vm is null)
        {
            return;
        }

        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in List.Columns)
        {
            c.SortDirection = null;
        }

        e.Column.SortDirection = direction;
        _vm.SortBy(e.Column.SortMemberPath, direction);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && ReferenceEquals(e.OriginalSource, List))
        {
            _vm.SetSelectedRows(List.SelectedItems);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && !shift && e.Key == Key.C)
        {
            // Ctrl+C copies the URL rather than the grid cells.
            _vm?.CopyUrlCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm?.ClearFilter();
            e.Handled = true;
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm?.ClearFilter();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            List.Focus();
            if (List.SelectedIndex < 0 && List.Items.Count > 0)
            {
                List.SelectedIndex = 0;
            }

            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusFilter();
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            CyclePanes(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    /// <summary>F6 cycles filter, list, request inspector, response inspector.</summary>
    private void CyclePanes(bool backwards)
    {
        var panes = new Func<bool>[]
        {
            () => FilterBox.Focus(),
            () =>
            {
                List.Focus();
                if (List.SelectedItem is { } item && List.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                {
                    row.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }

                return true;
            },
            () => InspectorPane.FocusRequest(),
            () => InspectorPane.FocusResponse(),
        };
        var current = FilterBox.IsKeyboardFocusWithin ? 0 : List.IsKeyboardFocusWithin ? 1 : InspectorPane.RequestHasFocus ? 2 : InspectorPane.ResponseHasFocus ? 3 : -1;
        var next = backwards ? (current <= 0 ? panes.Length - 1 : current - 1) : (current + 1) % panes.Length;
        panes[next]();
    }

    // ------------------------------------------------------------------ layout

    private void ApplyLayout()
    {
        var settings = AppServices.Current.Settings;
        MainGrid.RowDefinitions.Clear();
        MainGrid.ColumnDefinitions.Clear();
        var listStar = Math.Clamp(settings.ListPaneSize, 0.1, 0.9);
        if (settings.Layout == LayoutOrientation.SideBySide)
        {
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(listStar, GridUnitType.Star), MinWidth = 150 });
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - listStar, GridUnitType.Star), MinWidth = 200 });
            Place(List, 0, 0);
            Place(Splitter, 0, 1);
            Place(InspectorPane, 0, 2);
            Splitter.Width = 5;
            Splitter.Height = double.NaN;
            Splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            Splitter.VerticalAlignment = VerticalAlignment.Stretch;
            Splitter.ResizeDirection = GridResizeDirection.Columns;
        }
        else
        {
            MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(listStar, GridUnitType.Star), MinHeight = 80 });
            MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - listStar, GridUnitType.Star), MinHeight = 120 });
            Place(List, 0, 0);
            Place(Splitter, 1, 0);
            Place(InspectorPane, 2, 0);
            Splitter.Height = 5;
            Splitter.Width = double.NaN;
            Splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            Splitter.VerticalAlignment = VerticalAlignment.Stretch;
            Splitter.ResizeDirection = GridResizeDirection.Rows;
        }

        InspectorPane.ApplyLayout();
    }

    private static void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var settings = AppServices.Current.Settings;
        double first, last;
        if (settings.Layout == LayoutOrientation.SideBySide)
        {
            first = MainGrid.ColumnDefinitions[0].ActualWidth;
            last = MainGrid.ColumnDefinitions[2].ActualWidth;
        }
        else
        {
            first = MainGrid.RowDefinitions[0].ActualHeight;
            last = MainGrid.RowDefinitions[2].ActualHeight;
        }

        if (first + last > 0)
        {
            settings.ListPaneSize = first / (first + last);
            AppServices.Current.SaveSettings();
        }
    }

    private void OnDismissBanner(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Banner = null;
        }
    }

    /// <summary>Custom column values come from the row indexer, keyed by <c>kind:name</c>.</summary>
    private sealed class CustomValueConverter : IValueConverter
    {
        public static readonly CustomValueConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is EntryRowViewModel row && parameter is string key ? row[key] : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
