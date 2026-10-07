using System.Windows;
using System.Windows.Threading;
using Harborer.App.Services;
using Harborer.App.ViewModels;
using Harborer.App.Views;
using Harborer.Core.Settings;

namespace Harborer.App;

public partial class App : Application
{
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // State lives under %LOCALAPPDATA%\HARborer, or data\ beside the exe in portable mode.
        var paths = AppPaths.Resolve();
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"HARborer cannot create its state folder '{paths.Root}': {ex.Message}", "HARborer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        AppLog.Initialize(paths.LogsDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var settings = SettingsStore.Load(paths.SettingsFile);
        InitializeServices(this, paths, settings);
        AppLog.Info($"HARborer started (portable: {paths.IsPortable}, offline mode: {settings.OfflineMode})");

        var commandLine = CommandLine.Parse(e.Args);
        _main = new MainViewModel();
        var window = new MainWindow(_main);
        MainWindow = window;
        window.Show();

        if (commandLine.Error is not null)
        {
            Ui.Error(commandLine.Error);
        }

        _ = OpenStartupFilesAsync(commandLine);
    }

    /// <summary>Services, theme and shared resources. Also used by the UI smoke tests.</summary>
    public static void InitializeServices(Application app, AppPaths paths, AppSettings settings)
    {
        AppServices.Initialize(paths, settings);
        ThemeManager.Initialize(settings.Theme);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/HARborer;component/Themes/Common.xaml") });
        app.Resources["MonoFontSize"] = settings.MonoFontSize;
    }

    private async Task OpenStartupFilesAsync(CommandLine commandLine)
    {
        foreach (var file in commandLine.Files)
        {
            await _main!.OpenFileAsync(file, commandLine.Filter);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_main is not null)
        {
            foreach (var session in _main.Sessions)
            {
                session.Dispose();
            }

            AppServices.Current.SaveSettings();
        }

        AppLog.Info("HARborer exited");
        base.OnExit(e);
    }

    /// <summary>A local dialog with a copyable stack trace, written to the logs folder; nothing is transmitted.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled exception on the UI thread", e.Exception);
        e.Handled = true;
        try
        {
            CrashDialog.Show(e.Exception, AppLog.CurrentFile);
        }
        catch (Exception)
        {
            MessageBox.Show(e.Exception.ToString(), "HARborer error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary><c>HARborer.exe &lt;file.har&gt; [--filter "&lt;expr&gt;"]</c>.</summary>
public sealed class CommandLine
{
    public List<string> Files { get; } = [];

    public string? Filter { get; private set; }

    public string? Error { get; private set; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var result = new CommandLine();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--filter" or "-f" or "/filter")
            {
                if (i + 1 < args.Count)
                {
                    result.Filter = args[++i];
                }
                else
                {
                    result.Error = "--filter needs an expression, for example --filter \"status-code:5xx\".";
                }
            }
            else if (a.StartsWith("--filter=", StringComparison.Ordinal))
            {
                result.Filter = a["--filter=".Length..];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                result.Error = $"Unknown option '{a}'. Usage: HARborer.exe <file.har> [--filter \"<expr>\"]";
            }
            else
            {
                result.Files.Add(a);
            }
        }

        return result;
    }
}
