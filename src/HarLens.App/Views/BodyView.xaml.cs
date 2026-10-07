using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HarLens.App.ViewModels;

namespace HarLens.App.Views;

/// <summary>Shows the selected body sub-view. Visibility is switched in code to keep the XAML free of trigger chains.</summary>
public partial class BodyView : UserControl
{
    private BodyViewModel? _vm;

    public BodyView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as BodyViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }

            Update();
        };
    }

    /// <summary>Selects a search match in the Text view.</summary>
    public void HighlightText(int offset, int length)
    {
        if (_vm is null)
        {
            return;
        }

        _vm.SelectedView = BodyViewKind.Text;
        Dispatcher.BeginInvoke(() =>
        {
            TextEditor.Focus();
            TextEditor.Highlight(offset, length);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BodyViewModel.SelectedView) or nameof(BodyViewModel.PrettyKind))
        {
            Update();
        }
    }

    private void Update()
    {
        var view = _vm?.SelectedView ?? BodyViewKind.Text;
        var pretty = _vm?.PrettyKind ?? PrettyKind.None;
        PrettyPane.Visibility = Show(view == BodyViewKind.Pretty);
        TextEditor.Visibility = Show(view == BodyViewKind.Text);
        HexList.Visibility = Show(view == BodyViewKind.Hex);
        ImagePane.Visibility = Show(view == BodyViewKind.Image);
        JwtPane.Visibility = Show(view == BodyViewKind.Jwt);
        JsonTree.Visibility = Show(pretty == PrettyKind.JsonTree);
        PrettyEditor.Visibility = Show(pretty == PrettyKind.Code);
        FormGrid.Visibility = Show(pretty == PrettyKind.Form);
        PartsGrid.Visibility = Show(pretty == PrettyKind.Multipart);
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
