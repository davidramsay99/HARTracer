using System.Globalization;
using HarLens.Core.Http;
using HarLens.Core.Model;
using HarLens.Core.Text;

namespace HarLens.Core.Har;

/// <summary>HAR <c>timings</c>. A value of -1 means "not applicable" (SPEC 5.2).</summary>
public readonly record struct HarTimings(
    double Blocked,
    double Dns,
    double Connect,
    double Ssl,
    double Send,
    double Wait,
    double Receive)
{
    public static readonly HarTimings Unknown = new(-1, -1, -1, -1, -1, -1, -1);

    /// <summary>Connect time excluding TLS, since HAR includes <c>ssl</c> inside <c>connect</c>.</summary>
    public double TcpConnect => Connect < 0 ? -1 : Ssl > 0 ? Math.Max(0, Connect - Ssl) : Connect;

    /// <summary>Sum of applicable phases without double counting <c>ssl</c> (SPEC 5.2).</summary>
    public double Total
    {
        get
        {
            static double P(double v) => v > 0 ? v : 0;
            return P(Blocked) + P(Dns) + P(Connect) + P(Send) + P(Wait) + P(Receive);
        }
    }

    /// <summary>The seven waterfall phases in display order with TLS split out of connect.</summary>
    public IEnumerable<(string Phase, double Start, double Duration)> Phases()
    {
        double t = 0;
        foreach (var (name, value) in new[]
                 {
                     ("Blocked", Blocked), ("DNS", Dns), ("Connect", TcpConnect), ("TLS", Ssl),
                     ("Send", Send), ("Wait", Wait), ("Receive", Receive),
                 })
        {
            if (value >= 0)
            {
                yield return (name, t, value);
                t += value;
            }
        }
    }
}

public enum BodyTextEncoding : byte
{
    /// <summary>The JSON string is the text itself.</summary>
    None,

    /// <summary><c>encoding: "base64"</c>; the JSON string must be decoded before display (SPEC 5.2).</summary>
    Base64,

    /// <summary>An encoding value this application does not understand; shown undecoded.</summary>
    Other,
}

/// <summary>Location of a body string token inside a <see cref="HarSource"/>: offset and length of the JSON string including quotes.</summary>
public readonly record struct BodyRef(long Offset, int Length, BodyTextEncoding Encoding, bool IsEscaped)
{
    public static readonly BodyRef None = new(-1, 0, BodyTextEncoding.None, false);

    public bool Exists => Offset >= 0;

    /// <summary>Upper bound of the text length in UTF-8 bytes (the escaped token without its quotes).</summary>
    public int ApproximateLength => Exists ? Math.Max(0, Length - 2) : 0;
}

/// <summary>Display color marks for user annotations (SPEC 6.2).</summary>
public static class ColorMarks
{
    public static readonly IReadOnlyList<string> All = ["red", "orange", "yellow", "green", "blue", "purple"];
}

/// <summary>
/// One row of the index: the list columns, the headers, and byte offsets of the bodies (SPEC 5.3).
/// Everything else about the entry is read from <see cref="Source"/> on demand.
/// </summary>
public sealed class HarEntry
{
    private string _url = "";
    private string? _scheme;
    private string? _host;
    private int _port = -1;

    public HarEntry(HarSource source, int fileIndex, long offset, int length)
    {
        Source = source;
        FileIndex = fileIndex;
        Offset = offset;
        Length = length;
    }

    public HarSource Source { get; }

    /// <summary>Zero-based position in the source file's <c>entries</c> array (the hidden original-order column).</summary>
    public int FileIndex { get; }

    /// <summary>Byte offset of the entry object within <see cref="Source"/>.</summary>
    public long Offset { get; }

    /// <summary>Byte length of the entry object.</summary>
    public int Length { get; }

    /// <summary>One-based display number after sorting by start time. Assigned by the session.</summary>
    public int Id { get; set; }

    public string? PageRef { get; internal set; }

    public DateTimeOffset StartedDateTime { get; internal set; } = DateTimeOffset.MinValue;

    /// <summary>The entry's <c>time</c> field in milliseconds, or -1 when missing.</summary>
    public double Time { get; internal set; } = -1;

    public HarTimings Timings { get; internal set; } = HarTimings.Unknown;

    public string Method { get; internal set; } = "";

    public string Url
    {
        get => _url;
        internal set
        {
            _url = value;
            _scheme = null;
            _host = null;
        }
    }

    public string RequestHttpVersion { get; internal set; } = "";

    public HarHeader[] RequestHeaders { get; internal set; } = [];

    public long RequestHeadersSize { get; internal set; } = -1;

    public long RequestBodySize { get; internal set; } = -1;

    public string? RequestMimeType { get; internal set; }

    public BodyRef RequestBody { get; internal set; } = BodyRef.None;

    public bool HasPostParams { get; internal set; }

    public int Status { get; internal set; }

    public string StatusText { get; internal set; } = "";

    public string ResponseHttpVersion { get; internal set; } = "";

    public HarHeader[] ResponseHeaders { get; internal set; } = [];

    public long ResponseHeadersSize { get; internal set; } = -1;

    /// <summary>Response body bytes on the wire (<c>bodySize</c>), -1 when unknown.</summary>
    public long ResponseBodySize { get; internal set; } = -1;

    /// <summary>Decoded body length (<c>content.size</c>), -1 when missing.</summary>
    public long ContentSize { get; internal set; } = -1;

    /// <summary><c>content.compression</c>, -1 when missing.</summary>
    public long ContentCompression { get; internal set; } = -1;

    /// <summary>Raw <c>content.mimeType</c>, possibly with parameters.</summary>
    public string MimeType { get; internal set; } = "";

    public BodyRef ResponseBody { get; internal set; } = BodyRef.None;

    public string? RedirectUrl { get; internal set; }

    public string? ServerIPAddress { get; internal set; }

    public string? Connection { get; internal set; }

    // Vendor fields surfaced in the UI (SPEC 5.1).
    public string? ResourceType { get; internal set; }

    public string? Priority { get; internal set; }

    /// <summary><c>_fromCache</c> ("memory", "disk", "true") or Safari's cached <c>_fetchType</c>.</summary>
    public string? FromCache { get; internal set; }

    /// <summary><c>_transferSize</c>, -1 when missing.</summary>
    public long TransferSize { get; internal set; } = -1;

    public string? Error { get; internal set; }

    public string? InitiatorType { get; internal set; }

    public bool HasInitiator { get; internal set; }

    public int WebSocketMessageCount { get; internal set; }

    public string? SecurityState { get; internal set; }

    public bool HasCacheInfo { get; internal set; }

    // Annotations (SPEC 6.2), written back to `comment` and `_harlens` on save.
    public string? Comment { get; set; }

    public string? ColorMark { get; set; }

    /// <summary>The comment as stored in the source file.</summary>
    public string? OriginalComment { get; internal set; }

    /// <summary>The color mark as stored in the source file.</summary>
    public string? OriginalColorMark { get; internal set; }

    /// <summary>True when the comment or color differs from the source file, so saving must rewrite this entry.</summary>
    public bool AnnotationsChanged => !string.Equals(Comment, OriginalComment, StringComparison.Ordinal) ||
                                      !string.Equals(ColorMark, OriginalColorMark, StringComparison.Ordinal);

    /// <summary>UI flag: edited since the session was last saved.</summary>
    public bool AnnotationsDirty { get; set; }

    /// <summary>Source file label for merged sessions (SPEC 5.5).</summary>
    public string? SourceTag { get; set; }

    /// <summary>Cached resource category for the type chips, -1 until computed.</summary>
    internal int CategoryCache { get; set; } = -1;

    // Derived values.
    public bool IsFailed => Status == 0 || Error is not null;

    public bool IsFromCache => !string.IsNullOrEmpty(FromCache) &&
                               !string.Equals(FromCache, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>1 to 5 for 1xx to 5xx, 0 for failed or out-of-range status.</summary>
    public int StatusClass => Status is >= 100 and < 600 ? Status / 100 : 0;

    /// <summary>MIME type without parameters, lower case.</summary>
    public string MimeTypeBase => MimeTypes.StripParameters(MimeType);

    /// <summary>Response size column: bytes transferred when known, else the decoded size.</summary>
    public long ResponseSize
    {
        get
        {
            if (TransferSize >= 0)
            {
                return TransferSize;
            }

            if (ResponseBodySize >= 0 && ResponseHeadersSize >= 0)
            {
                return ResponseBodySize + ResponseHeadersSize;
            }

            if (ResponseBodySize > 0)
            {
                return ResponseBodySize;
            }

            return ContentSize;
        }
    }

    /// <summary>Bytes sent: headers plus body when known, else an estimate from the indexed headers and body token.</summary>
    public long RequestSize
    {
        get
        {
            long headers = RequestHeadersSize >= 0
                ? RequestHeadersSize
                : RequestHeaders.Sum(h => (long)h.Name.Length + h.Value.Length + 4) + Method.Length + Url.Length + 12;
            long body = RequestBodySize >= 0 ? RequestBodySize : RequestBody.ApproximateLength;
            return headers + body;
        }
    }

    /// <summary>Total time: the <c>time</c> field when present, else the sum of timings.</summary>
    public double TotalTime => Time >= 0 ? Time : Timings.Total;

    public DateTimeOffset EndDateTime => StartedDateTime.AddMilliseconds(Math.Max(0, TotalTime));

    /// <summary>Lower-case scheme. Set during indexing so filtering never parses URLs.</summary>
    public string Scheme
    {
        get
        {
            EnsureAuthority();
            return _scheme!;
        }
    }

    /// <summary>Host name without port, lower case.</summary>
    public string Host
    {
        get
        {
            EnsureAuthority();
            return _host!;
        }
    }

    /// <summary>Explicit port in the URL, or -1.</summary>
    public int Port
    {
        get
        {
            EnsureAuthority();
            return _port;
        }
    }

    /// <summary>Path and query, computed on demand (list column).</summary>
    public string Path => UrlParts.Split(_url).PathAndQuery;

    /// <summary>Host with port when the port is not the scheme default (Fiddler's Host column).</summary>
    public string HostDisplay => Port > 0 && !Http.UrlParts.IsDefaultPort(Scheme, Port)
        ? $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}"
        : Host;

    public string? GetRequestHeader(string name) => FindHeader(RequestHeaders, name);

    public string? GetResponseHeader(string name) => FindHeader(ResponseHeaders, name);

    public bool HasRequestHeader(string name) => FindHeader(RequestHeaders, name) is not null;

    public bool HasResponseHeader(string name) => FindHeader(ResponseHeaders, name) is not null;

    public override string ToString() => $"#{Id} {Method} {Url} {Status}";

    /// <summary>Sets the URL and its scheme, host and port, interning the repeated parts.</summary>
    internal void AssignUrl(string url, StringPool? pool)
    {
        _url = url;
        (_scheme, _host, _port) = UrlParts.SplitAuthority(url, pool);
    }

    private void EnsureAuthority()
    {
        if (_host is null || _scheme is null)
        {
            // Entries built outside the indexer. Benign race: every thread computes the same values.
            var (scheme, host, port) = UrlParts.SplitAuthority(_url, null);
            _port = port;
            _host = host;
            _scheme = scheme;
        }
    }

    /// <summary>A copy for another session (merge), so display numbers and annotations stay per tab.</summary>
    public HarEntry CopyForSession() => (HarEntry)MemberwiseClone();

    /// <summary>Stable key of the entry within its source, used for caches and replay provenance.</summary>
    public string Key => $"{Source.Id}:{Offset}";

    internal static string? FindHeader(HarHeader[] headers, string name)
    {
        foreach (var h in headers)
        {
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return h.Value;
            }
        }

        return null;
    }
}
