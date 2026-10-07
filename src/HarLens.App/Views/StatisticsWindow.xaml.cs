using System.Windows;
using HarLens.App.ViewModels;

namespace HarLens.App.Views;

public partial class StatisticsWindow : Window
{
    public StatisticsWindow(StatisticsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
