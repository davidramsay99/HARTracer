using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Harborer.Core.Settings;

namespace Harborer.App.Tests;

/// <summary>Runs a test body on an STA thread with a WPF Application and dispatcher, failing on any dispatcher exception.</summary>
internal static class UiHost
{
    public static void Run(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var errors = new List<Exception>();
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
                {
                    errors.Add(e.Exception);
                    e.Handled = true;
                };
                var root = Path.Combine(Path.GetTempPath(), "harborer-ui-" + Guid.NewGuid().ToString("N"));
                var paths = new AppPaths(root, isPortable: false);
                paths.EnsureCreated();
                AppLog.Initialize(paths.LogsDirectory);
                App.InitializeServices(app, paths, new AppSettings());

                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var task = body();
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                var timeout = new DispatcherTimer(TimeSpan.FromMinutes(3), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
                Dispatcher.PushFrame(frame);
                timeout.Stop();
                if (!task.IsCompleted)
                {
                    throw new TimeoutException("UI test did not finish in three minutes.");
                }

                task.GetAwaiter().GetResult();
                if (errors.Count > 0)
                {
                    throw new AggregateException("Dispatcher exceptions during the UI test", errors);
                }
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                foreach (var w in Application.Current?.Windows.OfType<Window>().ToList() ?? [])
                {
                    w.Close();
                }

                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>Processes dispatcher work until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20_000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Lets layout and rendering run.</summary>
    public static async Task Settle(int ms = 150)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
