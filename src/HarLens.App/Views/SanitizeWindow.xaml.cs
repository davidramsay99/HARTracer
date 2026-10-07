using System.Windows;
using HarLens.App.ViewModels;

namespace HarLens.App.Views;

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
