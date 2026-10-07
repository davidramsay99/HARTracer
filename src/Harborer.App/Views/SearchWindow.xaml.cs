using System.Windows;
using Harborer.App.ViewModels;

namespace Harborer.App.Views;

public partial class SearchWindow : Window
{
    public SearchWindow(SearchViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => QueryBox.Focus();
        Closed += (_, _) => vm.CancelCommand.Execute(null);
    }
}
