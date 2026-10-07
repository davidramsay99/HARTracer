using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarLens.Core.Har;

public sealed class HarWriteOptions
{
    /// <summary>Entries to write. Null writes the whole session.</summary>
    public IReadOnlyCollection<HarEntry>? Entries { get; init; }

    public bool Indented { get; init; } = true;

    /// <summary>Write user annotations to <c>comment</c> and <c>_harlens</c>.</summary>
    public bool WriteAnnotations { get; init; } = true;

    /// <summary>
    /// Optional last-stage rewrite of each entry's JSON (the sanitizer). Return null to keep the bytes unchanged.
    /// </summary>
    public Func<HarEntry, byte[], byte[]?>? EntryTransform { get; init; }

    /// <summary>Optional rewrite of each <c>log</c> property value (name, raw JSON) and of each page.</summary>
    public Func<string, byte[], byte[]?>? LogPropertyTransform { get; init; }

    public IProgress<double>? Progress { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Writes HAR 1.2. Unchanged entries are copied byte for byte from their source, so unknown and vendor fields
/// survive. Only entries with annotations, merge tags or renamed pages are rewritten.
/// </summary>
public static class HarWriter
{
    public static readonly string CreatorName = "HarLens";

    public static string CreatorVersion =>
        typeof(HarWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
    };

    internal static readonly JsonSerializerOptions NodeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Saves to a file. Refuses to overwrite any file the session reads from (never modified in place).</summary>
    public static void Save(HarSession session, string path, HarWriteOptions? options = null)
    {
        EnsureNotSource(session, path);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Write(session, stream, options ?? new HarWriteOptions());
    }

    public static void EnsureNotSource(HarSession session, string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var d in session.Documents)
        {
            if (d.Source.FilePath is { } src && string.Equals(Path.GetFullPath(src), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new IOException($"'{Path.GetFileName(path)}' is open as a source file. Choose a different file name: HarLens never overwrites an open HAR.");
            }
        }
    }

    public static void Write(HarSession session, Stream output, HarWriteOptions options)
    {
        var selected = options.Entries is null ? session.Entries.ToList() : options.Entries.ToList();
        var isSubset = options.Entries is not null && selected.Count != session.Entries.Count;
        var entries = session.Kind == SessionKind.File
            ? selected.OrderBy(e => e.FileIndex).ToList()
            : selected.OrderBy(e => e.Id).ToList();

        var pageRemap = new Dictionary<(HarSource, string), string>();
        var pages = SelectPages(session, entries, isSubset, pageRemap);

        using var writer = new Utf8JsonWriter(output, options.Indented ? WriterOptions : WriterOptions with { Indented = false });
        writer.WriteStartObject();
        writer.WritePropertyName("log");
        writer.WriteStartObject();

        var log = session.Log;
        if (log is not null)
        {
            var entriesWritten = false;
            for (var i = 0; i < log.LogProperties.Count; i++)
            {
                if (i == log.EntriesPosition)
                {
                    WriteEntries(writer, session, entries, pageRemap, options);
                    entriesWritten = true;
                }

                var prop = log.LogProperties[i];
                var value = prop.Name == "pages" && (isSubset || pageRemap.Count > 0) ? PagesArray(pages) : prop.RawValue;
                WriteProperty(writer, prop.Name, value, options);
            }

            if (!entriesWritten)
            {
                WriteEntries(writer, session, entries, pageRemap, options);
            }
        }
        else
        {
            WriteProperty(writer, "version", Encoding.UTF8.GetBytes("\"1.2\""), options);
            var creator = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["name"] = CreatorName,
                ["version"] = CreatorVersion,
            });
            WriteProperty(writer, "creator", creator, options);
            if (pages.Count > 0)
            {
                WriteProperty(writer, "pages", PagesArray(pages), options);
            }

            WriteEntries(writer, session, entries, pageRemap, options);
        }

        writer.WriteEndObject();
        if (log is not null)
        {
            foreach (var root in log.RootProperties)
            {
                writer.WritePropertyName(root.Name);
                writer.WriteRawValue(root.RawValue, skipInputValidation: true);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        options.Progress?.Report(1);
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, byte[] raw, HarWriteOptions options)
    {
        var value = options.LogPropertyTransform?.Invoke(name, raw) ?? raw;
        writer.WritePropertyName(name);
        writer.WriteRawValue(value, skipInputValidation: true);
    }

    private static void WriteEntries(Utf8JsonWriter writer, HarSession session, List<HarEntry> entries,
        Dictionary<(HarSource, string), string> pageRemap, HarWriteOptions options)
    {
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        var tagSources = session.Kind == SessionKind.Merged;
        for (var i = 0; i < entries.Count; i++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            var raw = entry.Source.ReadBytes(entry.Offset, entry.Length);
            string? newPageRef = null;
            var remap = entry.PageRef is not null && pageRemap.TryGetValue((entry.Source, entry.PageRef), out newPageRef);
            var annotate = options.WriteAnnotations && entry.AnnotationsChanged;
            var tag = tagSources && entry.SourceTag is not null;
            if (annotate || tag || remap)
            {
                var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 4096 })!.AsObject();
                if (annotate)
                {
                    ApplyAnnotations(node, entry);
                }

                if (tag)
                {
                    HarLensObject(node)["source"] = entry.SourceTag;
                }

                if (remap)
                {
                    node["pageref"] = newPageRef;
                }

                raw = JsonSerializer.SerializeToUtf8Bytes(node, NodeOptions);
            }

            if (options.EntryTransform?.Invoke(entry, raw) is { } transformed)
            {
                raw = transformed;
            }

            writer.WriteRawValue(raw, skipInputValidation: true);
            if ((i & 0xFF) == 0)
            {
                options.Progress?.Report(entries.Count == 0 ? 1 : (double)i / entries.Count);
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>Writes comment and color into the entry: HAR <c>comment</c> and the <c>_harlens</c> vendor object.</summary>
    public static void ApplyAnnotations(JsonObject node, HarEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Comment))
        {
            node.Remove("comment");
        }
        else
        {
            node["comment"] = entry.Comment;
        }

        if (string.IsNullOrEmpty(entry.ColorMark))
        {
            if (node["_harlens"] is JsonObject existing)
            {
                existing.Remove("color");
                if (existing.Count == 0)
                {
                    node.Remove("_harlens");
                }
            }
        }
        else
        {
            HarLensObject(node)["color"] = entry.ColorMark;
        }
    }

    private static JsonObject HarLensObject(JsonObject node)
    {
        if (node["_harlens"] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        node["_harlens"] = created;
        return created;
    }

    private static List<(HarPage Page, string Id)> SelectPages(HarSession session, List<HarEntry> entries, bool isSubset,
        Dictionary<(HarSource, string), string> remap)
    {
        var referenced = new HashSet<(HarSource, string)>();
        foreach (var e in entries)
        {
            if (e.PageRef is not null)
            {
                referenced.Add((e.Source, e.PageRef));
            }
        }

        var result = new List<(HarPage, string)>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in session.Pages)
        {
            var key = (page.Source!, page.Id);
            if (isSubset && !referenced.Contains(key))
            {
                continue;
            }

            var id = page.Id;
            if (!usedIds.Add(id))
            {
                // Two merged files used the same page id: give the later one a unique id and remap its entries.
                var n = 2;
                while (!usedIds.Add($"{page.Id}_{n}"))
                {
                    n++;
                }

                id = $"{page.Id}_{n}";
                remap[key] = id;
            }

            result.Add((page, id));
        }

        return result;
    }

    private static byte[] PagesArray(List<(HarPage Page, string Id)> pages)
    {
        var array = new JsonArray();
        foreach (var (page, id) in pages)
        {
            var node = JsonNode.Parse(page.RawJson)!;
            if (id != page.Id && node is JsonObject obj)
            {
                obj["id"] = id;
            }

            array.Add(node);
        }

        return JsonSerializer.SerializeToUtf8Bytes(array, NodeOptions);
    }
}
