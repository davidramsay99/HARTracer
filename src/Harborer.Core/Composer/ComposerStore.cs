using System.Text.Json;
using Harborer.Core.Http;
using Harborer.Core.Settings;

namespace Harborer.Core.Composer;

public sealed class HistoryItem
{
    public DateTimeOffset Sent { get; set; }

    public HttpRequestSpec Request { get; set; } = new();

    public int? Status { get; set; }

    public double? TimeMs { get; set; }

    /// <summary>True when credential header values were removed before the item was stored.</summary>
    public bool CredentialsRemoved { get; set; }

    public string Title => $"{Request.Method} {Request.Url}";
}

public sealed class SavedRequest
{
    public string Name { get; set; } = "";

    public HttpRequestSpec Request { get; set; } = new();
}

/// <summary>A named group of saved requests, stored as one JSON file under <c>collections\</c>.</summary>
public sealed class RequestCollection
{
    public string Name { get; set; } = "";

    public List<SavedRequest> Requests { get; set; } = [];
}

/// <summary>Composer history and collections on local disk. History is clearable and, by default, stored without credential values.</summary>
public sealed class ComposerStore
{
    public const int MaxHistory = 500;

    private readonly AppPaths _paths;

    public ComposerStore(AppPaths paths)
    {
        _paths = paths;
    }

    public List<HistoryItem> LoadHistory()
    {
        try
        {
            return File.Exists(_paths.HistoryFile)
                ? JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(_paths.HistoryFile), SettingsStore.JsonOptions) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            AppLog.Warning("Composer history could not be read: " + ex.Message);
            return [];
        }
    }

    public void AppendHistory(HistoryItem item, bool keepCredentials)
    {
        if (!keepCredentials)
        {
            item.Request = item.Request.Clone();
            foreach (var h in item.Request.Headers.Where(h => ReplayGuard.CredentialHeaders.Contains(h.Name) && h.Value.Length > 0))
            {
                h.Value = "";
                item.CredentialsRemoved = true;
            }

            if (item.Request.Options.ClientCertificate is { } cert)
            {
                cert.Password = null;
            }
        }

        var history = LoadHistory();
        history.Insert(0, item);
        if (history.Count > MaxHistory)
        {
            history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        }

        Write(_paths.HistoryFile, history);
    }

    public void ClearHistory()
    {
        if (File.Exists(_paths.HistoryFile))
        {
            File.Delete(_paths.HistoryFile);
        }
    }

    public List<RequestCollection> LoadCollections()
    {
        var list = new List<RequestCollection>();
        if (!Directory.Exists(_paths.CollectionsDirectory))
        {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.CollectionsDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<RequestCollection>(File.ReadAllText(file), SettingsStore.JsonOptions) is { } collection)
                {
                    list.Add(collection);
                }
            }
            catch (JsonException ex)
            {
                AppLog.Warning($"Collection file {file} could not be read: {ex.Message}");
            }
        }

        return list;
    }

    public void SaveCollection(RequestCollection collection) =>
        Write(Path.Combine(_paths.CollectionsDirectory, SafeFileName(collection.Name) + ".json"), collection);

    public void DeleteCollection(string name)
    {
        var path = Path.Combine(_paths.CollectionsDirectory, SafeFileName(name) + ".json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "collection" : cleaned;
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, SettingsStore.JsonOptions));
    }
}
