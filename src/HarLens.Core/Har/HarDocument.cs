namespace HarLens.Core.Har;

/// <summary>A <c>log.pages</c> item. <see cref="RawJson"/> preserves the original object for lossless save.</summary>
public sealed class HarPage
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public DateTimeOffset StartedDateTime { get; init; } = DateTimeOffset.MinValue;

    /// <summary><c>pageTimings.onContentLoad</c> in ms, -1 when missing.</summary>
    public double OnContentLoad { get; init; } = -1;

    /// <summary><c>pageTimings.onLoad</c> in ms, -1 when missing.</summary>
    public double OnLoad { get; init; } = -1;

    public byte[] RawJson { get; init; } = [];

    public HarSource? Source { get; init; }
}

/// <summary>A raw top-level property (inside <c>log</c> or at the root) kept verbatim for re-export.</summary>
public sealed record RawProperty(string Name, byte[] RawValue);

/// <summary>Everything in <c>log</c> other than <c>entries</c>.</summary>
public sealed class HarLogInfo
{
    public string? Version { get; set; }

    public string? CreatorName { get; set; }

    public string? CreatorVersion { get; set; }

    public string? BrowserName { get; set; }

    public string? BrowserVersion { get; set; }

    public string? Comment { get; set; }

    /// <summary>All <c>log</c> properties except <c>entries</c>, in file order, as raw JSON values.</summary>
    public List<RawProperty> LogProperties { get; } = [];

    /// <summary>Position of <c>entries</c> among <see cref="LogProperties"/> (entries are written back there).</summary>
    public int EntriesPosition { get; set; } = -1;

    /// <summary>Root-level properties other than <c>log</c>, preserved for re-export.</summary>
    public List<RawProperty> RootProperties { get; } = [];
}

public enum HarDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record HarDiagnostic(HarDiagnosticSeverity Severity, string Message)
{
    public override string ToString() => $"{Severity}: {Message}";
}

/// <summary>Where and why parsing stopped.</summary>
public sealed class HarParseFailure
{
    public required string Message { get; init; }

    public long ByteOffset { get; init; }

    /// <summary>1-based line of the failure.</summary>
    public long Line { get; init; }

    /// <summary>1-based column of the failure, in bytes.</summary>
    public long Column { get; init; }

    /// <summary>JSON path of the failure, for example <c>$.log.entries[41].response.content.text</c>.</summary>
    public string JsonPath { get; init; } = "$";

    /// <summary>True when the file ended before the JSON was complete.</summary>
    public bool Truncated { get; init; }

    public int RecoveredEntries { get; init; }

    public override string ToString() =>
        $"{Message} at line {Line}, column {Column} (byte offset {ByteOffset}), path {JsonPath}. " +
        $"{RecoveredEntries} complete entr{(RecoveredEntries == 1 ? "y" : "ies")} recovered.";
}

/// <summary>A parsed HAR: the index plus the log-level information needed to write it back.</summary>
public sealed class HarDocument : IDisposable
{
    public HarDocument(HarSource source)
    {
        Source = source;
    }

    public HarSource Source { get; }

    public HarLogInfo Log { get; } = new();

    public List<HarPage> Pages { get; } = [];

    /// <summary>Entries in file order.</summary>
    public List<HarEntry> Entries { get; } = [];

    public void Dispose() => Source.Dispose();
}

public sealed class HarLoadResult
{
    public HarDocument? Document { get; init; }

    public List<HarDiagnostic> Diagnostics { get; } = [];

    /// <summary>Set when parsing stopped early. Entries before the failure are still in <see cref="Document"/>.</summary>
    public HarParseFailure? Failure { get; set; }

    /// <summary>Set when nothing could be loaded (not JSON, not a HAR, unreadable).</summary>
    public string? FatalError { get; set; }

    public bool Success => Document is not null && FatalError is null;

    public IEnumerable<string> Warnings => Diagnostics.Where(d => d.Severity >= HarDiagnosticSeverity.Warning).Select(d => d.Message);
}

public sealed class HarLoadProgress
{
    public long BytesRead { get; init; }

    public long TotalBytes { get; init; }

    public int EntriesRead { get; init; }

    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesRead / TotalBytes, 0, 1);
}
