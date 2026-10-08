namespace Harborer.Core.Har;

public enum SessionKind
{
    /// <summary>A single opened file.</summary>
    File,

    /// <summary>Entries combined from several files.</summary>
    Merged,

    /// <summary>Requests sent from the composer.</summary>
    Composer,
}

/// <summary>
/// The working set behind one tab: entries sorted by start time with display numbers assigned, the pages,
/// and the documents whose sources the entries read from. Documents are reference counted because a
/// merged session shares them with the tabs it was built from.
/// </summary>
public sealed class HarSession : IDisposable
{
    private readonly List<HarDocument> _documents = [];
    private readonly List<HarEntry> _entries = [];
    private bool _disposed;

    private HarSession(string name, SessionKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; set; }

    public SessionKind Kind { get; }

    /// <summary>Path the session was loaded from, for single-file sessions.</summary>
    public string? FilePath => Kind == SessionKind.File && _documents.Count == 1 ? _documents[0].Source.FilePath : null;

    public IReadOnlyList<HarDocument> Documents => _documents;

    /// <summary>Entries sorted by <c>startedDateTime</c>; <see cref="HarEntry.Id"/> is the one-based position.</summary>
    public IReadOnlyList<HarEntry> Entries => _entries;

    public List<HarPage> Pages { get; } = [];

    /// <summary>The single document's log information, or null for merged and composer sessions.</summary>
    public HarLogInfo? Log => Kind == SessionKind.File && _documents.Count == 1 ? _documents[0].Log : null;

    public DateTimeOffset FirstStart => _entries.Count == 0
        ? DateTimeOffset.MinValue
        : _entries.Where(e => e.StartedDateTime != DateTimeOffset.MinValue).Select(e => e.StartedDateTime).DefaultIfEmpty(DateTimeOffset.MinValue).Min();

    public DateTimeOffset LastEnd => _entries.Count == 0
        ? DateTimeOffset.MinValue
        : _entries.Where(e => e.StartedDateTime != DateTimeOffset.MinValue).Select(e => e.EndDateTime).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

    public bool HasUnsavedAnnotations => _entries.Any(e => e.AnnotationsDirty);

    public static HarSession FromDocument(HarDocument document, string? name = null)
    {
        var session = new HarSession(name ?? document.Source.DisplayName, SessionKind.File);
        session.Adopt(document);
        session._entries.AddRange(document.Entries);
        session.Pages.AddRange(document.Pages);
        session.SortAndNumber();
        return session;
    }

    /// <summary>Creates an empty composer session backed by an in-memory source.</summary>
    public static HarSession CreateComposer(string name = "Composer")
    {
        var session = new HarSession(name, SessionKind.Composer);
        var document = new HarDocument(new MemoryHarSource(name));
        document.Log.Version = "1.2";
        session.Adopt(document);
        return session;
    }

    /// <summary>Combines sessions into one, tagging each entry with its source file.</summary>
    public static HarSession Merge(IReadOnlyList<HarSession> sessions, string name)
    {
        var merged = new HarSession(name, SessionKind.Merged);
        foreach (var session in sessions)
        {
            foreach (var document in session._documents)
            {
                if (!merged._documents.Contains(document))
                {
                    merged.Adopt(document);
                }
            }

            foreach (var entry in session._entries)
            {
                var copy = entry.CopyForSession();
                copy.SourceTag ??= session.Kind == SessionKind.Merged ? entry.SourceTag : session.Name;
                merged._entries.Add(copy);
            }

            foreach (var page in session.Pages)
            {
                if (!merged.Pages.Contains(page))
                {
                    merged.Pages.Add(page);
                }
            }
        }

        merged.SortAndNumber();
        return merged;
    }

    /// <summary>The in-memory source of a composer session, which new entries are appended to.</summary>
    public MemoryHarSource ComposerSource =>
        Kind == SessionKind.Composer ? (MemoryHarSource)_documents[0].Source : throw new InvalidOperationException("Not a composer session.");

    /// <summary>Appends entries (composer sends) keeping start-time order.</summary>
    public void Append(IEnumerable<HarEntry> entries)
    {
        _entries.AddRange(entries);
        SortAndNumber();
    }

    public HarPage? FindPage(string? id) => id is null ? null : Pages.FirstOrDefault(p => p.Id == id);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var document in _documents)
        {
            DocumentRefCount.Release(document);
        }

        _documents.Clear();
        _entries.Clear();
    }

    private void Adopt(HarDocument document)
    {
        DocumentRefCount.AddRef(document);
        _documents.Add(document);
    }

    private void SortAndNumber()
    {
        var order = new Dictionary<HarSource, int>();
        foreach (var d in _documents)
        {
            order[d.Source] = order.Count;
        }

        _entries.Sort((a, b) =>
        {
            var c = a.StartedDateTime.UtcTicks.CompareTo(b.StartedDateTime.UtcTicks);
            if (c != 0)
            {
                return c;
            }

            c = order.GetValueOrDefault(a.Source).CompareTo(order.GetValueOrDefault(b.Source));
            return c != 0 ? c : a.FileIndex.CompareTo(b.FileIndex);
        });
        for (var i = 0; i < _entries.Count; i++)
        {
            _entries[i].Id = i + 1;
        }
    }

    private static class DocumentRefCount
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HarDocument, Counter> s_counts = new();

        public static void AddRef(HarDocument document) => Interlocked.Increment(ref s_counts.GetOrCreateValue(document).Value);

        public static void Release(HarDocument document)
        {
            if (s_counts.TryGetValue(document, out var counter) && Interlocked.Decrement(ref counter.Value) <= 0)
            {
                document.Dispose();
            }
        }

        private sealed class Counter
        {
            public int Value;
        }
    }
}
