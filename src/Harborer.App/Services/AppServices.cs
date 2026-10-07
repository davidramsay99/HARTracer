using Harborer.Core.Composer;
using Harborer.Core.Har;
using Harborer.Core.Settings;

namespace Harborer.App.Services;

/// <summary>Process-wide state shared by the view models. Created once at startup.</summary>
public sealed class AppServices
{
    private static AppServices? s_current;

    private AppServices(AppPaths paths, AppSettings settings)
    {
        Paths = paths;
        Settings = settings;
        ComposerStore = new ComposerStore(paths);
    }

    public static AppServices Current => s_current ?? throw new InvalidOperationException("AppServices not initialized.");

    public AppPaths Paths { get; }

    public AppSettings Settings { get; }

    public ComposerStore ComposerStore { get; }

    /// <summary>Decoded bodies shared by all tabs (bounded LRU).</summary>
    public BodyCache BodyCache { get; } = new(256L * 1024 * 1024);

    /// <summary>Hosts for which the replay prompt is suppressed in this run only.</summary>
    public ReplayGuardSuppressions ReplaySuppressions { get; } = new();

    /// <summary>Raised when display settings (time, size, mask, font) change so views can refresh.</summary>
    public event EventHandler? DisplaySettingsChanged;

    public static AppServices Initialize(AppPaths paths, AppSettings settings)
    {
        s_current = new AppServices(paths, settings);
        return s_current;
    }

    public void NotifyDisplaySettingsChanged() => DisplaySettingsChanged?.Invoke(this, EventArgs.Empty);

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Paths.SettingsFile, Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving settings failed: " + ex.Message);
        }
    }
}
