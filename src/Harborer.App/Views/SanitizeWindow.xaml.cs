using System.Windows;
using Harborer.App.ViewModels;

namespace Harborer.App.Views;

public partial class SanitizeWindow : Window
{
    public SanitizeWindow(SanitizeViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Completed += (_, _) => Services.Ui.Info(vm.Status, "Sanitized export");
        Closing += (_, _) => vm.CancelCommand.Execute(null);
    }
}
