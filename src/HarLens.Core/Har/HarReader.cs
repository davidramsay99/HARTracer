using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace HarLens.Core.Har;

/// <summary>Opens HAR 1.1 and 1.2 files (.har, .json, .har.gz) into an index (SPEC 5).</summary>
public static class HarReader
{
    /// <summary>Opens a file. Gzip content is detected by its magic bytes and decompressed in memory; no temporary file is written.</summary>
    public static HarLoadResult Load(string path, IProgress<HarLoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        HarSource source;
        try
        {
            source = OpenSource(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            var failed = new HarLoadResult { FatalError = $"Cannot read '{path}': {ex.Message}" };
            return failed;
        }

        var result = Load(source, progress, cancellationToken);
        if (result.Document is null)
        {
            source.Dispose();
        }

        return result;
    }

    public static HarLoadResult Load(HarSource source, IProgress<HarLoadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        new HarScanner(source, progress, cancellationToken).Run();

    /// <summary>Parses HAR bytes held in memory, for tests, SAZ conversion and clipboard input.</summary>
    public static HarLoadResult LoadBytes(ReadOnlySpan<byte> bytes, string displayName) =>
        Load(new MemoryHarSource(displayName, bytes));

    private static HarSource OpenSource(string path, CancellationToken cancellationToken)
    {
        Span<byte> head = stackalloc byte[4];
        int headLength;
        using (var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            headLength = probe.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        var name = Path.GetFileName(path);
        var full = Path.GetFullPath(path);
        if (headLength >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            var memory = new MemoryHarSource(name, full) { WasCompressed = true };
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            memory.AppendFrom(gzip, cancellationToken);
            return memory;
        }

        if (headLength >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
        {
            // UTF-16 with BOM: transcode to UTF-8 in memory.
            var text = File.ReadAllText(path, Encoding.Unicode);
            return new MemoryHarSource(name, Encoding.UTF8.GetBytes(text), full);
        }

        return new FileHarSource(path);
    }
}

/// <summary>
/// One streaming pass with <see cref="Utf8JsonReader"/> over the source. Each entry object is located with
/// <c>TrySkip</c> (growing the buffer until the whole entry is present) and then indexed from a contiguous span.
/// </summary>
internal sealed class HarScanner
{
    private const int InitialBufferSize = 1 << 20;
    private const string NotHarMessage = "The file is valid JSON but does not contain log.entries, so it is not a HAR file.";

    private readonly HarSource _source;
    private readonly IProgress<HarLoadProgress>? _progress;
    private readonly CancellationToken _ct;
    private readonly HarDocument _doc;
    private readonly HarLoadResult _result;
    private readonly EntryIndexer _indexer = new();

    private Stream _stream = Stream.Null;
    private byte[] _buffer = new byte[InitialBufferSize];
    private int _dataEnd;
    private long _bufferAbs;
    private bool _eof;
    private int _readerBase;
    private JsonReaderState _state = new(JsonRead.ReaderOptions);

    private long _newlinesBeforeBuffer;
    private long _lastNewlineAbsBeforeBuffer = -1;
    private long _lastGoodAbs;
    private long _lastProgressBytes;

    // Structural position for error reporting.
    private string _section = "$";
    private int _entryIndex = -1;
    private int _skipStartIdx = -1;
    private string? _skipPath;
    private string? _valuePath;

    private bool _sawLog;
    private bool _sawEntries;

    public HarScanner(HarSource source, IProgress<HarLoadProgress>? progress, CancellationToken ct)
    {
        _source = source;
        _progress = progress;
        _ct = ct;
        _doc = new HarDocument(source);
        _result = new HarLoadResult { Document = _doc };
    }

    public HarLoadResult Run()
    {
        try
        {
            _stream = _source.OpenSequential();
            Fill();
            if (_dataEnd >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
            {
                _readerBase = 3;
            }

            var reader = NewReader();
            ParseRoot(ref reader);
        }
        catch (JsonException ex)
        {
            return Fail(ex.Message);
        }
        catch (HarStructureException ex)
        {
            return Fatal(ex.Message);
        }
        catch (IOException ex)
        {
            return Fail("Read error: " + ex.Message);
        }
        finally
        {
            _stream.Dispose();
        }

        if (!_sawLog || !_sawEntries)
        {
            return Fatal(NotHarMessage);
        }

        Finish();
        return _result;
    }

    private void Finish()
    {
        var version = _doc.Log.Version;
        if (string.IsNullOrEmpty(version))
        {
            _result.Diagnostics.Add(new HarDiagnostic(HarDiagnosticSeverity.Warning, "log.version is missing; loaded as HAR 1.2."));
        }
        else if (version is not ("1.1" or "1.2"))
        {
            _result.Diagnostics.Add(new HarDiagnostic(HarDiagnosticSeverity.Warning, $"Unknown log.version '{version}'; loaded as HAR 1.2."));
        }

        ReportProgress(force: true);
    }

    private HarLoadResult Fatal(string message)
    {
        _doc.Entries.Clear();
        return new HarLoadResult { FatalError = message, Document = null };
    }

    private HarLoadResult Fail(string message)
    {
        var (offset, path, truncated) = LocateFailure();
        var (line, column) = LineAndColumn(offset);
        var failure = new HarParseFailure
        {
            Message = truncated ? "The file ends before the JSON is complete" : "Invalid JSON: " + message,
            ByteOffset = offset,
            Line = line,
            Column = column,
            JsonPath = path,
            Truncated = truncated,
            RecoveredEntries = _doc.Entries.Count,
        };

        if (!_sawEntries)
        {
            // Nothing usable was reached: report the parse error as fatal, naming line, column and path (SPEC 5.4).
            return new HarLoadResult { FatalError = failure.ToString(), Failure = failure };
        }

        _result.Failure = failure;
        _result.Diagnostics.Add(new HarDiagnostic(HarDiagnosticSeverity.Error, failure.ToString()));
        Finish();
        return _result;
    }

    private void ParseRoot(ref Utf8JsonReader reader)
    {
        if (!Read(ref reader))
        {
            throw new HarStructureException("The file is empty.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            // Validate the rest so a non-JSON file reports a parse error rather than "not a HAR".
            SkipCurrent(ref reader, "$");
            while (Read(ref reader))
            {
            }

            throw new HarStructureException(NotHarMessage);
        }

        while (Read(ref reader) && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString() ?? "";
            _valuePath = "$." + name;
            Read(ref reader);
            _valuePath = null;
            if (name == "log" && reader.TokenType == JsonTokenType.StartObject && !_sawLog)
            {
                _sawLog = true;
                _section = "$.log";
                ParseLog(ref reader);
                _section = "$";
            }
            else
            {
                var raw = CaptureValue(ref reader, "$." + name);
                _doc.Log.RootProperties.Add(new RawProperty(name, raw));
            }
        }

        // Trailing content after the root object is reported by the reader as a JsonException.
        while (Read(ref reader))
        {
        }
    }

    private void ParseLog(ref Utf8JsonReader reader)
    {
        var log = _doc.Log;
        while (Read(ref reader) && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString() ?? "";
            _valuePath = "$.log." + name;
            Read(ref reader);
            _valuePath = null;
            if (name == "entries" && !_sawEntries)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new HarStructureException("log.entries is not an array, so the file is not a HAR file.");
                }

                _sawEntries = true;
                log.EntriesPosition = log.LogProperties.Count;
                _section = "$.log.entries";
                ParseEntries(ref reader);
                _section = "$.log";
                continue;
            }

            var raw = CaptureValue(ref reader, "$.log." + name);
            log.LogProperties.Add(new RawProperty(name, raw));
            InterpretLogProperty(name, raw);
        }
    }

    private void ParseEntries(ref Utf8JsonReader reader)
    {
        var index = 0;
        while (Read(ref reader) && reader.TokenType != JsonTokenType.EndArray)
        {
            _entryIndex = index;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                _result.Diagnostics.Add(new HarDiagnostic(HarDiagnosticSeverity.Warning, $"log.entries[{index}] is not an object and was skipped."));
                SkipCurrent(ref reader, $"$.log.entries[{index}]");
                index++;
                continue;
            }

            var (startIdx, length) = SkipCurrent(ref reader, null);
            var abs = _bufferAbs + startIdx;
            var entry = _indexer.Index(_source, _buffer.AsSpan(startIdx, length), abs, index);
            _doc.Entries.Add(entry);
            index++;
            if ((index & 0x3FF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
                ReportProgress(force: false);
            }
        }

        _entryIndex = -1;
    }

    /// <summary>Captures the current value as raw bytes (whole object or array, or the scalar token).</summary>
    private byte[] CaptureValue(ref Utf8JsonReader reader, string path)
    {
        var (start, length) = SkipCurrent(ref reader, path);
        return _buffer.AsSpan(start, length).ToArray();
    }

    /// <summary>
    /// Moves past the current value, refilling and growing the buffer until it is complete in memory.
    /// Returns the buffer index and length of the value; valid until the next read.
    /// </summary>
    private (int Start, int Length) SkipCurrent(ref Utf8JsonReader reader, string? path)
    {
        var startIdx = _readerBase + (int)reader.TokenStartIndex;
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            var end = _readerBase + (int)reader.BytesConsumed;
            return (startIdx, end - startIdx);
        }

        _skipStartIdx = startIdx;
        _skipPath = path;
        var stateAfterStart = reader.CurrentState;
        while (true)
        {
            var probe = reader;
            if (probe.TrySkip())
            {
                reader = probe;
                break;
            }

            // Keep everything from the value's first byte, then read more (growing the buffer if needed).
            ShiftAndFill(startIdx);
            startIdx = 0;
            _skipStartIdx = 0;
            _readerBase = 1;
            reader = new Utf8JsonReader(_buffer.AsSpan(1, _dataEnd - 1), _eof, stateAfterStart);
        }

        var endIdx = _readerBase + (int)reader.BytesConsumed;
        _lastGoodAbs = _bufferAbs + endIdx;
        _skipStartIdx = -1;
        _skipPath = null;
        return (startIdx, endIdx - startIdx);
    }

    private bool Read(ref Utf8JsonReader reader)
    {
        while (!reader.Read())
        {
            if (_eof)
            {
                return false;
            }

            _state = reader.CurrentState;
            var keep = _readerBase + (int)reader.BytesConsumed;
            ShiftAndFill(keep);
            _readerBase = 0;
            reader = new Utf8JsonReader(_buffer.AsSpan(0, _dataEnd), _eof, _state);
        }

        _lastGoodAbs = _bufferAbs + _readerBase + reader.BytesConsumed;
        return true;
    }

    private Utf8JsonReader NewReader() => new(_buffer.AsSpan(_readerBase, _dataEnd - _readerBase), _eof, _state);

    private void ShiftAndFill(int keepFrom)
    {
        if (keepFrom > 0)
        {
            var discarded = _buffer.AsSpan(0, keepFrom);
            var newlines = discarded.Count((byte)'\n');
            if (newlines > 0)
            {
                _newlinesBeforeBuffer += newlines;
                _lastNewlineAbsBeforeBuffer = _bufferAbs + discarded.LastIndexOf((byte)'\n');
            }

            var remaining = _dataEnd - keepFrom;
            Buffer.BlockCopy(_buffer, keepFrom, _buffer, 0, remaining);
            _bufferAbs += keepFrom;
            _dataEnd = remaining;
        }

        if (_dataEnd > _buffer.Length / 2)
        {
            // Large value in progress: grow so that each retry scans proportionally more data.
            Array.Resize(ref _buffer, checked(_buffer.Length * 2));
        }

        Fill();
    }

    private void Fill()
    {
        while (_dataEnd < _buffer.Length)
        {
            var read = _stream.Read(_buffer, _dataEnd, _buffer.Length - _dataEnd);
            if (read <= 0)
            {
                _eof = true;
                break;
            }

            _dataEnd += read;
        }

        ReportProgress(force: false);
    }

    private void ReportProgress(bool force)
    {
        if (_progress is null)
        {
            return;
        }

        var bytes = _bufferAbs + _dataEnd;
        if (!force && bytes - _lastProgressBytes < 4 * 1024 * 1024)
        {
            return;
        }

        _lastProgressBytes = bytes;
        _progress.Report(new HarLoadProgress { BytesRead = bytes, TotalBytes = _source.Length, EntriesRead = _doc.Entries.Count });
    }

    private void InterpretLogProperty(string name, byte[] raw)
    {
        var log = _doc.Log;
        try
        {
            switch (name)
            {
                case "version":
                    log.Version = ReadScalar(raw);
                    break;
                case "comment":
                    log.Comment = ReadScalar(raw);
                    break;
                case "creator":
                    (log.CreatorName, log.CreatorVersion) = ReadNameVersion(raw);
                    break;
                case "browser":
                    (log.BrowserName, log.BrowserVersion) = ReadNameVersion(raw);
                    break;
                case "pages":
                    ReadPages(raw);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _result.Diagnostics.Add(new HarDiagnostic(HarDiagnosticSeverity.Warning, $"log.{name} could not be read: {ex.Message}"));
        }
    }

    private static string? ReadScalar(byte[] raw)
    {
        var r = new Utf8JsonReader(raw, JsonRead.ReaderOptions);
        r.Read();
        return JsonRead.String(ref r);
    }

    private static (string? Name, string? Version) ReadNameVersion(byte[] raw)
    {
        using var doc = JsonDocument.Parse(raw, DocumentOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (Prop(doc.RootElement, "name"), Prop(doc.RootElement, "version"));
    }

    private void ReadPages(byte[] raw)
    {
        using var doc = JsonDocument.Parse(raw, DocumentOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var page in doc.RootElement.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double onContentLoad = -1, onLoad = -1;
            if (page.TryGetProperty("pageTimings", out var timings) && timings.ValueKind == JsonValueKind.Object)
            {
                onContentLoad = Number(timings, "onContentLoad");
                onLoad = Number(timings, "onLoad");
            }

            _doc.Pages.Add(new HarPage
            {
                Id = Prop(page, "id") ?? "",
                Title = Prop(page, "title") ?? "",
                StartedDateTime = JsonRead.ParseDate(Prop(page, "startedDateTime")),
                OnContentLoad = onContentLoad,
                OnLoad = onLoad,
                RawJson = System.Text.Encoding.UTF8.GetBytes(page.GetRawText()),
                Source = _source,
            });
        }
    }

    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 4096,
    };

    private static string? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null,
        } : null;

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : -1;

    /// <summary>
    /// Finds the absolute offset and JSON path of a parse failure by re-scanning the value being read.
    /// The data counts as truncated when it is a valid JSON prefix that simply runs out at end of file.
    /// </summary>
    private (long Offset, string Path, bool Truncated) LocateFailure()
    {
        var endAbs = _bufferAbs + _dataEnd;
        int scanStart;
        string prefix;
        if (_skipStartIdx >= 0)
        {
            scanStart = _skipStartIdx;
            prefix = _skipPath ?? (_entryIndex >= 0 ? $"$.log.entries[{_entryIndex}]" : _section);
        }
        else
        {
            // Between values: skip separators and re-scan whatever follows the last good token.
            var idx = (int)Math.Clamp(Math.Max(_lastGoodAbs, _bufferAbs) - _bufferAbs, 0, _dataEnd);
            while (idx < _dataEnd && _buffer[idx] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)':')
            {
                idx++;
            }

            scanStart = idx;
            prefix = _valuePath ?? (_entryIndex >= 0 ? $"$.log.entries[{_entryIndex}]" : _section);
            if (idx >= _dataEnd)
            {
                return (endAbs, prefix, _eof);
            }
        }

        var span = _buffer.AsSpan(scanStart, Math.Max(0, _dataEnd - scanStart));
        var tracker = new PathTracker(prefix);
        var r = new Utf8JsonReader(span, isFinalBlock: false, new JsonReaderState(JsonRead.ReaderOptions));
        long lastGood = 0;
        try
        {
            while (r.Read())
            {
                tracker.OnToken(ref r);
                lastGood = r.BytesConsumed;
            }

            // Ran out of data without a syntax error: the JSON is cut short.
            return (_eof ? endAbs : SkipWhitespace(_bufferAbs + scanStart + lastGood), tracker.Path, _eof);
        }
        catch (JsonException)
        {
            return (SkipWhitespace(_bufferAbs + scanStart + lastGood), tracker.Path, false);
        }
    }

    private long SkipWhitespace(long abs)
    {
        var idx = abs - _bufferAbs;
        while (idx >= 0 && idx < _dataEnd && _buffer[idx] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            idx++;
        }

        return _bufferAbs + Math.Min(idx, _dataEnd);
    }

    private (long Line, long Column) LineAndColumn(long abs)
    {
        var withinBuffer = (int)Math.Clamp(abs - _bufferAbs, 0, _dataEnd);
        var span = _buffer.AsSpan(0, withinBuffer);
        var newlines = span.Count((byte)'\n');
        var line = _newlinesBeforeBuffer + newlines + 1;
        var lastNewline = newlines > 0 ? _bufferAbs + span.LastIndexOf((byte)'\n') : _lastNewlineAbsBeforeBuffer;
        return (line, abs - lastNewline);
    }

    /// <summary>Tracks the JSON path while re-scanning a value for error reporting.</summary>
    private sealed class PathTracker(string prefix)
    {
        private readonly List<Frame> _stack = [];

        public string Path
        {
            get
            {
                var sb = new StringBuilder(prefix);
                for (var i = 1; i < _stack.Count; i++)
                {
                    AppendChild(sb, _stack[i - 1]);
                }

                if (_stack.Count > 0)
                {
                    AppendChild(sb, _stack[^1]);
                }

                return sb.ToString();
            }
        }

        public void OnToken(ref Utf8JsonReader r)
        {
            switch (r.TokenType)
            {
                case JsonTokenType.PropertyName:
                    _stack[^1].Property = r.GetString();
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (_stack.Count > 0 && _stack[^1].IsArray)
                    {
                        _stack[^1].Index++;
                    }

                    _stack.Add(new Frame { IsArray = r.TokenType == JsonTokenType.StartArray });
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    _stack.RemoveAt(_stack.Count - 1);
                    break;
                default:
                    if (_stack.Count > 0 && _stack[^1].IsArray)
                    {
                        _stack[^1].Index++;
                    }

                    break;
            }
        }

        private static void AppendChild(StringBuilder sb, Frame frame)
        {
            if (frame.IsArray)
            {
                if (frame.Index >= 0)
                {
                    sb.Append('[').Append(frame.Index).Append(']');
                }
            }
            else if (frame.Property is not null)
            {
                sb.Append('.').Append(frame.Property);
            }
        }

        private sealed class Frame
        {
            public bool IsArray { get; init; }

            public int Index { get; set; } = -1;

            public string? Property { get; set; }
        }
    }
}

internal sealed class HarStructureException(string message) : Exception(message);
