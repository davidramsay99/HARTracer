using System.Windows;
using System.Windows.Threading;
using HarLens.App.Services;
using HarLens.App.ViewModels;
using HarLens.App.Views;
using HarLens.Core.Settings;

namespace HarLens.App;

public partial class App : Application
{
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // State lives under %LOCALAPPDATA%\HarLens, or data\ beside the exe in portable mode (SPEC 3.6, 10).
        var paths = AppPaths.Resolve();
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"HarLens cannot create its state folder '{paths.Root}': {ex.Message}", "HarLens", MessageBoxButton.OK, MessageBoxImage.Error);
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
        AppServices.Initialize(paths, settings);
        AppLog.Info($"HarLens started (portable: {paths.IsPortable}, offline mode: {settings.OfflineMode})");

        ThemeManager.Initialize(settings.Theme);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/HarLens;component/Themes/Common.xaml") });
        Resources["MonoFontSize"] = settings.MonoFontSize;

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

        AppLog.Info("HarLens exited");
        base.OnExit(e);
    }

    /// <summary>SPEC 10: a local dialog with a copyable stack trace, written to the logs folder; nothing is transmitted.</summary>
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
            MessageBox.Show(e.Exception.ToString(), "HarLens error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary><c>HarLens.exe &lt;file.har&gt; [--filter "&lt;expr&gt;"]</c> (SPEC 9).</summary>
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
                result.Error = $"Unknown option '{a}'. Usage: HarLens.exe <file.har> [--filter \"<expr>\"]";
            }
            else
            {
                result.Files.Add(a);
            }
        }

        return result;
    }
}
