namespace HarLens.Core.Settings;

/// <summary>
/// Where state lives: <c>%LOCALAPPDATA%\HarLens\</c>, or a <c>data\</c> folder beside the executable
/// when a <c>portable.flag</c> file is present there.
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    public AppPaths(string root, bool isPortable)
    {
        Root = root;
        IsPortable = isPortable;
    }

    public string Root { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string CollectionsDirectory => Path.Combine(Root, "collections");

    public string HistoryFile => Path.Combine(Root, "composer-history.json");

    /// <summary>Resolves the state root for an executable directory.</summary>
    public static AppPaths Resolve(string? executableDirectory = null)
    {
        var exeDir = executableDirectory ?? AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, PortableFlagName)))
        {
            return new AppPaths(Path.Combine(exeDir, "data"), isPortable: true);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return new AppPaths(Path.Combine(local, "HarLens"), isPortable: false);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CollectionsDirectory);
    }
}

/// <summary>
/// The application log: file paths and error text only. Callers must never pass header values,
/// bodies or URLs with query strings. Nothing is transmitted.
/// </summary>
public static class AppLog
{
    private static readonly Lock s_lock = new();
    private static string? s_directory;

    public static void Initialize(string logsDirectory) => s_directory = logsDirectory;

    public static string? CurrentFile => s_directory is null ? null : Path.Combine(s_directory, $"harlens-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        var file = CurrentFile;
        if (file is null)
        {
            return;
        }

        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(s_directory!);
                File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the application down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
