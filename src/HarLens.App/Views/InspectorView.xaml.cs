using System.Windows;
using System.Windows.Controls;
using HarLens.App.Services;
using HarLens.App.ViewModels;
using HarLens.Core.Search;

namespace HarLens.App.Views;

public partial class InspectorView : UserControl
{
    private SearchHit? _pendingHit;
    private InspectorViewModel? _vm;

    public InspectorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.EntryShown -= OnEntryShown;
            }

            _vm = DataContext as InspectorViewModel;
            if (_vm is not null)
            {
                _vm.EntryShown += OnEntryShown;
            }
        };
    }

    public bool RequestHasFocus => RequestTabs.IsKeyboardFocusWithin;

    public bool ResponseHasFocus => ResponseTabs.IsKeyboardFocusWithin;

    public bool FocusRequest() => RequestTabs.Focus();

    public bool FocusResponse() => ResponseTabs.Focus();

    public void ApplyLayout()
    {
        var ratio = Math.Clamp(AppServices.Current.Settings.RequestPaneSize, 0.1, 0.9);
        RequestRow.Height = new GridLength(ratio, GridUnitType.Star);
        ResponseRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var total = RequestRow.ActualHeight + ResponseRow.ActualHeight;
        if (total > 0)
        {
            AppServices.Current.Settings.RequestPaneSize = RequestRow.ActualHeight / total;
            AppServices.Current.SaveSettings();
        }
    }

    /// <summary>Jumps to the tab that holds a search hit and highlights the match.</summary>
    public void ShowSearchHit(SearchHit hit)
    {
        _pendingHit = hit;
        if (_vm?.Row?.Entry == hit.Entry && _vm.Request.Headers.Count + _vm.Response.Headers.Count > 0)
        {
            Dispatcher.BeginInvoke(ApplyPendingHit, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnEntryShown(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ApplyPendingHit, System.Windows.Threading.DispatcherPriority.Background);

    private void ApplyPendingHit()
    {
        if (_pendingHit is not { } hit || _vm?.Row?.Entry != hit.Entry)
        {
            return;
        }

        _pendingHit = null;
        switch (hit.Scope)
        {
            case SearchScope.Url:
            case SearchScope.RequestHeaders:
                RequestTabs.SelectedItem = RequestHeadersTab;
                SelectHeader(RequestHeadersGrid, hit.HeaderIndex);
                break;
            case SearchScope.ResponseHeaders:
                ResponseTabs.SelectedItem = ResponseHeadersTab;
                SelectHeader(ResponseHeadersGrid, hit.HeaderIndex);
                break;
            case SearchScope.RequestBody:
                RequestTabs.SelectedItem = RequestBodyTab;
                RequestBodyView.HighlightText(hit.Index, hit.Length);
                break;
            case SearchScope.ResponseBody:
                ResponseTabs.SelectedItem = ResponseBodyTab;
                ResponseBodyView.HighlightText(hit.Index, hit.Length);
                break;
        }
    }

    private static void SelectHeader(DataGrid grid, int index)
    {
        if (index >= 0 && index < grid.Items.Count)
        {
            grid.SelectedIndex = index;
            grid.ScrollIntoView(grid.Items[index]);
        }
    }
}
