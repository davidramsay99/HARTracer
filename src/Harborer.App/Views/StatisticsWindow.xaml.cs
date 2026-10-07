using System.Windows;
using Harborer.App.ViewModels;

namespace Harborer.App.Views;

public partial class StatisticsWindow : Window
{
    public StatisticsWindow(StatisticsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
