using System.Windows;
using HarLens.App.ViewModels;

namespace HarLens.App.Views;

public partial class CompareWindow : Window
{
    public CompareWindow(CompareViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
