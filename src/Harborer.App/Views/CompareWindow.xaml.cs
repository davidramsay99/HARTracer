using System.Windows;
using Harborer.App.ViewModels;

namespace Harborer.App.Views;

public partial class CompareWindow : Window
{
    public CompareWindow(CompareViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
