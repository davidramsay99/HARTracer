using System.Globalization;
using System.Windows;
using HarLens.App.Services;
using HarLens.App.ViewModels;

namespace HarLens.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        var paths = AppServices.Current.Paths;
        CapBox.Text = (vm.Settings.ResponseSizeCapBytes / (1024.0 * 1024)).ToString("0.##", CultureInfo.CurrentCulture);
        StorageText.Text = $"Settings, history, collections and logs are stored in {paths.Root}" +
                           (paths.IsPortable ? " (portable mode: portable.flag is beside the executable)." : ". Place a portable.flag file beside HarLens.exe to keep them in a data folder next to it instead.");
    }

    private void OnCapLostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CapBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var mb) && mb > 0)
        {
            AppServices.Current.Settings.ResponseSizeCapBytes = (long)(mb * 1024 * 1024);
            AppServices.Current.SaveSettings();
        }
    }
}
