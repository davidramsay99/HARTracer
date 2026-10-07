using System.Windows;
using HarLens.App.Services;
using HarLens.App.ViewModels;

namespace HarLens.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        var s = vm.Settings;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) &&
            s.WindowLeft >= SystemParameters.VirtualScreenLeft && s.WindowTop >= SystemParameters.VirtualScreenTop &&
            s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (s.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        Closing += (_, e) =>
        {
            var unsaved = vm.Sessions.Where(t => t.HasUnsavedChanges).Select(t => t.Title).ToList();
            if (unsaved.Count > 0 && !Ui.Confirm($"These tabs have annotations that are not saved: {string.Join(", ", unsaved)}.\n\nExit anyway?"))
            {
                e.Cancel = true;
                return;
            }

            s.WindowMaximized = WindowState == WindowState.Maximized;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
            AppServices.Current.SaveSettings();
            foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w != this).ToList())
            {
                w.Close();
            }
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(File.Exists))
            {
                await _vm.OpenFileAsync(file);
            }
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnFocusFilter(object sender, RoutedEventArgs e) => SessionView.FocusFilterOfActive();

    private void OnFilterHelp(object sender, RoutedEventArgs e) => Ui.Info(
        "Terms are combined with AND. A leading '-' negates a term. Free text matches the URL.\n\n" +
        "status-code:403   status-code:5xx   method:POST   domain:*.contoso.com   scheme:https\n" +
        "mime-type:application/json   larger-than:100k   smaller-than:1M\n" +
        "has-response-header:x-azure-ref   has-request-header:authorization   header:x-cache=TCP_MISS\n" +
        "is:from-cache   is:failed   is:annotated   time-greater-than:2000   time-less-than:50\n" +
        "resource-type:fetch   url:token   priority:High   remote-address:10.*\n" +
        "set-cookie-name:sid   cookie-name:session   comment:slow   color:red   source:a.har\n" +
        "/regex against url/   -mime-type:image/png",
        "Filter syntax");

    private void OnShortcutHelp(object sender, RoutedEventArgs e) => Ui.Info(
        "Ctrl+O open    Ctrl+W close tab    Ctrl+S save HAR\n" +
        "Ctrl+F find in the current view    Ctrl+Shift+F search all entries\n" +
        "Ctrl+L focus filter    Esc clear filter    Up/Down move between entries\n" +
        "Ctrl+C copy URL    Ctrl+Shift+C copy as cURL    Ctrl+R send to composer\n" +
        "Ctrl+Enter send (composer)    F6 cycle panes    Ctrl+Plus / Ctrl+Minus font size",
        "Keyboard shortcuts");
}
