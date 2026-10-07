using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborer.Core.Settings;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

public enum TimeDisplay
{
    RelativeToFirst,
    LocalTime,
    Utc,
}

public enum SizeDisplay
{
    HumanReadable,
    Bytes,
}

public enum LayoutOrientation
{
    /// <summary>List left, inspector right.</summary>
    SideBySide,

    /// <summary>Inspector beneath the list.</summary>
    Stacked,
}

public sealed class ColumnSetting
{
    public string Key { get; set; } = "";

    public int DisplayIndex { get; set; }

    public double Width { get; set; }

    public bool Visible { get; set; } = true;
}

/// <summary>A user column bound to a header or a vendor field.</summary>
public sealed class CustomColumn
{
    /// <summary>"request-header", "response-header", "header" (either side) or "field".</summary>
    public string Kind { get; set; } = "response-header";

    /// <summary>Header name, or a field name / dotted path such as <c>_priority</c> or <c>response._transferSize</c>.</summary>
    public string Name { get; set; } = "";

    public string Key => $"{Kind}:{Name}";

    public string Header => Kind switch
    {
        "request-header" => $"{Name} (req)",
        "field" => Name,
        _ => Name,
    };
}

public sealed class FilterPreset
{
    public string Name { get; set; } = "";

    public string Expression { get; set; } = "";
}

/// <summary>
/// Everything in <c>settings.json</c>: human-readable, and tolerant of unknown keys, which are kept
/// and written back so that a newer version's settings survive an older version.
/// </summary>
public sealed class AppSettings
{
    /// <summary>ON at first run. While ON the Send button is disabled and Harborer.Net is never loaded.</summary>
    public bool OfflineMode { get; set; } = true;

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public bool MaskSecrets { get; set; }

    public TimeDisplay TimeDisplay { get; set; } = TimeDisplay.RelativeToFirst;

    public SizeDisplay SizeDisplay { get; set; } = SizeDisplay.HumanReadable;

    public double MonoFontSize { get; set; } = 12;

    public bool WordWrap { get; set; }

    public LayoutOrientation Layout { get; set; } = LayoutOrientation.SideBySide;

    public double ListPaneSize { get; set; } = 0.55;

    public double RequestPaneSize { get; set; } = 0.45;

    public bool GroupByPage { get; set; }

    public List<ColumnSetting> Columns { get; set; } = [];

    public List<CustomColumn> CustomColumns { get; set; } = [];

    public List<string> RecentFiles { get; set; } = [];

    public int MaxRecentFiles { get; set; } = 15;

    public List<FilterPreset> FilterPresets { get; set; } = [];

    public long ResponseSizeCapBytes { get; set; } = 100L * 1024 * 1024;

    public double DefaultTimeoutSeconds { get; set; } = 100;

    public List<string> SanitizeExtraHeaders { get; set; } = [];

    public List<string> SanitizeExtraParameters { get; set; } = [];

    public List<string> SanitizePatterns { get; set; } = [];

    public double WindowLeft { get; set; } = double.NaN;

    public double WindowTop { get; set; } = double.NaN;

    public double WindowWidth { get; set; } = 1400;

    public double WindowHeight { get; set; } = 860;

    public bool WindowMaximized { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; set; }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        }
    }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads settings. A missing file gives defaults; an unreadable one is kept aside as <c>settings.json.bad</c> and defaults are used.</summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            AppLog.Warning($"settings.json could not be read ({ex.Message}); defaults are in use. The file was kept as settings.json.bad.");
            try
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }

            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
