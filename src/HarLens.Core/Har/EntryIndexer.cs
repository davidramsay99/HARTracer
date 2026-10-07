using System.Text.Json;
using HarLens.Core.Model;

namespace HarLens.Core.Har;

/// <summary>
/// Builds the index row for one complete entry object. Reads only the list columns, headers and body
/// offsets; everything else is skipped and read later from the source on demand (SPEC 5.3).
/// </summary>
internal sealed class EntryIndexer
{
    private readonly StringPool _pool = new();
    private readonly List<HarHeader> _scratch = new(64);

    public HarEntry Index(HarSource source, ReadOnlySpan<byte> entryJson, long absoluteOffset, int fileIndex)
    {
        var entry = new HarEntry(source, fileIndex, absoluteOffset, entryJson.Length);
        var r = new Utf8JsonReader(entryJson, JsonRead.ReaderOptions);
        r.Read();
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("request"u8))
            {
                r.Read();
                ParseRequest(ref r, entry, absoluteOffset);
            }
            else if (r.ValueTextEquals("response"u8))
            {
                r.Read();
                ParseResponse(ref r, entry, absoluteOffset);
            }
            else if (r.ValueTextEquals("startedDateTime"u8))
            {
                r.Read();
                entry.StartedDateTime = JsonRead.Date(ref r);
            }
            else if (r.ValueTextEquals("time"u8))
            {
                r.Read();
                entry.Time = JsonRead.Double(ref r);
            }
            else if (r.ValueTextEquals("timings"u8))
            {
                r.Read();
                entry.Timings = ParseTimings(ref r);
            }
            else if (r.ValueTextEquals("pageref"u8))
            {
                r.Read();
                entry.PageRef = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("serverIPAddress"u8))
            {
                r.Read();
                entry.ServerIPAddress = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("connection"u8))
            {
                r.Read();
                entry.Connection = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("comment"u8))
            {
                r.Read();
                entry.Comment = NullIfEmpty(JsonRead.String(ref r));
            }
            else if (r.ValueTextEquals("cache"u8))
            {
                r.Read();
                entry.HasCacheInfo = HasContent(ref r);
            }
            else if (r.ValueTextEquals("_initiator"u8))
            {
                r.Read();
                ParseInitiator(ref r, entry);
            }
            else if (r.ValueTextEquals("_priority"u8))
            {
                r.Read();
                entry.Priority = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("_resourceType"u8))
            {
                r.Read();
                entry.ResourceType = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("_fromCache"u8))
            {
                r.Read();
                entry.FromCache = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("_fetchType"u8))
            {
                // Safari: "Network Load", "Memory Cache", "Disk Cache".
                r.Read();
                var fetchType = JsonRead.Pooled(ref r, _pool);
                if (fetchType is not null && fetchType.Contains("cache", StringComparison.OrdinalIgnoreCase))
                {
                    entry.FromCache = fetchType;
                }
            }
            else if (r.ValueTextEquals("_transferSize"u8))
            {
                r.Read();
                entry.TransferSize = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("_error"u8))
            {
                r.Read();
                entry.Error = NullIfEmpty(JsonRead.String(ref r));
            }
            else if (r.ValueTextEquals("_webSocketMessages"u8))
            {
                r.Read();
                entry.WebSocketMessageCount = CountArray(ref r);
            }
            else if (r.ValueTextEquals("_securityState"u8))
            {
                r.Read();
                entry.SecurityState = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("_harlens"u8))
            {
                r.Read();
                ParseHarLens(ref r, entry);
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        entry.OriginalComment = entry.Comment;
        entry.OriginalColorMark = entry.ColorMark;
        return entry;
    }

    private void ParseRequest(ref Utf8JsonReader r, HarEntry entry, long abs)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return;
        }

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("method"u8))
            {
                r.Read();
                entry.Method = JsonRead.Pooled(ref r, _pool) ?? "";
            }
            else if (r.ValueTextEquals("url"u8))
            {
                r.Read();
                entry.AssignUrl(JsonRead.String(ref r) ?? "", _pool);
            }
            else if (r.ValueTextEquals("httpVersion"u8))
            {
                r.Read();
                entry.RequestHttpVersion = JsonRead.Pooled(ref r, _pool) ?? "";
            }
            else if (r.ValueTextEquals("headers"u8))
            {
                r.Read();
                entry.RequestHeaders = ParseHeaders(ref r);
            }
            else if (r.ValueTextEquals("headersSize"u8))
            {
                r.Read();
                entry.RequestHeadersSize = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("bodySize"u8))
            {
                r.Read();
                entry.RequestBodySize = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("postData"u8))
            {
                r.Read();
                ParsePostData(ref r, entry, abs);
            }
            else if (r.ValueTextEquals("_error"u8))
            {
                r.Read();
                entry.Error ??= NullIfEmpty(JsonRead.String(ref r));
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }
    }

    private void ParsePostData(ref Utf8JsonReader r, HarEntry entry, long abs)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return;
        }

        long offset = -1;
        var length = 0;
        var escaped = false;
        var encoding = BodyTextEncoding.None;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("mimeType"u8))
            {
                r.Read();
                entry.RequestMimeType = JsonRead.Pooled(ref r, _pool);
            }
            else if (r.ValueTextEquals("text"u8))
            {
                r.Read();
                if (r.TokenType == JsonTokenType.String)
                {
                    offset = abs + r.TokenStartIndex;
                    length = checked((int)(r.BytesConsumed - r.TokenStartIndex));
                    escaped = r.ValueIsEscaped;
                }
                else
                {
                    r.Skip();
                }
            }
            else if (r.ValueTextEquals("encoding"u8))
            {
                r.Read();
                encoding = ParseEncoding(JsonRead.String(ref r));
            }
            else if (r.ValueTextEquals("params"u8))
            {
                r.Read();
                entry.HasPostParams = CountArray(ref r) > 0;
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        if (offset >= 0)
        {
            entry.RequestBody = new BodyRef(offset, length, encoding, escaped);
        }
    }

    private void ParseResponse(ref Utf8JsonReader r, HarEntry entry, long abs)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return;
        }

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("status"u8))
            {
                r.Read();
                entry.Status = (int)Math.Clamp(JsonRead.Int64(ref r, 0), int.MinValue, int.MaxValue);
            }
            else if (r.ValueTextEquals("statusText"u8))
            {
                r.Read();
                entry.StatusText = JsonRead.Pooled(ref r, _pool) ?? "";
            }
            else if (r.ValueTextEquals("httpVersion"u8))
            {
                r.Read();
                entry.ResponseHttpVersion = JsonRead.Pooled(ref r, _pool) ?? "";
            }
            else if (r.ValueTextEquals("headers"u8))
            {
                r.Read();
                entry.ResponseHeaders = ParseHeaders(ref r);
            }
            else if (r.ValueTextEquals("content"u8))
            {
                r.Read();
                ParseContent(ref r, entry, abs);
            }
            else if (r.ValueTextEquals("redirectURL"u8))
            {
                r.Read();
                entry.RedirectUrl = NullIfEmpty(JsonRead.String(ref r));
            }
            else if (r.ValueTextEquals("headersSize"u8))
            {
                r.Read();
                entry.ResponseHeadersSize = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("bodySize"u8))
            {
                r.Read();
                entry.ResponseBodySize = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("_transferSize"u8))
            {
                r.Read();
                entry.TransferSize = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("_error"u8))
            {
                r.Read();
                entry.Error ??= NullIfEmpty(JsonRead.String(ref r));
            }
            else if (r.ValueTextEquals("_fromCache"u8))
            {
                r.Read();
                entry.FromCache ??= JsonRead.Pooled(ref r, _pool);
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }
    }

    private void ParseContent(ref Utf8JsonReader r, HarEntry entry, long abs)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return;
        }

        long offset = -1;
        var length = 0;
        var escaped = false;
        var encoding = BodyTextEncoding.None;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("size"u8))
            {
                r.Read();
                entry.ContentSize = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("compression"u8))
            {
                r.Read();
                entry.ContentCompression = JsonRead.Int64(ref r);
            }
            else if (r.ValueTextEquals("mimeType"u8))
            {
                r.Read();
                entry.MimeType = JsonRead.Pooled(ref r, _pool) ?? "";
            }
            else if (r.ValueTextEquals("text"u8))
            {
                r.Read();
                if (r.TokenType == JsonTokenType.String)
                {
                    offset = abs + r.TokenStartIndex;
                    length = checked((int)(r.BytesConsumed - r.TokenStartIndex));
                    escaped = r.ValueIsEscaped;
                }
                else
                {
                    r.Skip();
                }
            }
            else if (r.ValueTextEquals("encoding"u8))
            {
                r.Read();
                encoding = ParseEncoding(JsonRead.String(ref r));
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        if (offset >= 0)
        {
            entry.ResponseBody = new BodyRef(offset, length, encoding, escaped);
        }
    }

    private HarHeader[] ParseHeaders(ref Utf8JsonReader r)
    {
        if (r.TokenType != JsonTokenType.StartArray)
        {
            r.Skip();
            return [];
        }

        _scratch.Clear();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
            {
                r.Skip();
                continue;
            }

            string name = "";
            string value = "";
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("name"u8))
                {
                    r.Read();
                    name = JsonRead.Pooled(ref r, _pool) ?? "";
                }
                else if (r.ValueTextEquals("value"u8))
                {
                    r.Read();
                    value = JsonRead.Pooled(ref r, _pool, 200) ?? "";
                }
                else
                {
                    r.Read();
                    r.Skip();
                }
            }

            _scratch.Add(new HarHeader(name, value));
        }

        return _scratch.Count == 0 ? [] : _scratch.ToArray();
    }

    private static HarTimings ParseTimings(ref Utf8JsonReader r)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return HarTimings.Unknown;
        }

        double blocked = -1, dns = -1, connect = -1, ssl = -1, send = -1, wait = -1, receive = -1;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            ref double target = ref blocked;
            var known = true;
            if (r.ValueTextEquals("blocked"u8))
            {
                target = ref blocked;
            }
            else if (r.ValueTextEquals("dns"u8))
            {
                target = ref dns;
            }
            else if (r.ValueTextEquals("connect"u8))
            {
                target = ref connect;
            }
            else if (r.ValueTextEquals("ssl"u8))
            {
                target = ref ssl;
            }
            else if (r.ValueTextEquals("send"u8))
            {
                target = ref send;
            }
            else if (r.ValueTextEquals("wait"u8))
            {
                target = ref wait;
            }
            else if (r.ValueTextEquals("receive"u8))
            {
                target = ref receive;
            }
            else
            {
                known = false;
            }

            r.Read();
            if (known)
            {
                target = JsonRead.Double(ref r);
            }
            else
            {
                r.Skip();
            }
        }

        return new HarTimings(blocked, dns, connect, ssl, send, wait, receive);
    }

    private void ParseInitiator(ref Utf8JsonReader r, HarEntry entry)
    {
        if (r.TokenType == JsonTokenType.Null)
        {
            return;
        }

        entry.HasInitiator = true;
        if (r.TokenType != JsonTokenType.StartObject)
        {
            entry.InitiatorType = JsonRead.Pooled(ref r, _pool);
            return;
        }

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("type"u8))
            {
                r.Read();
                entry.InitiatorType = JsonRead.Pooled(ref r, _pool);
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }
    }

    private void ParseHarLens(ref Utf8JsonReader r, HarEntry entry)
    {
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return;
        }

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("color"u8))
            {
                r.Read();
                entry.ColorMark = NullIfEmpty(JsonRead.Pooled(ref r, _pool));
            }
            else if (r.ValueTextEquals("source"u8))
            {
                r.Read();
                entry.SourceTag = NullIfEmpty(JsonRead.Pooled(ref r, _pool));
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }
    }

    /// <summary>Counts array elements and leaves the reader on the closing token.</summary>
    private static int CountArray(ref Utf8JsonReader r)
    {
        if (r.TokenType != JsonTokenType.StartArray)
        {
            r.Skip();
            return 0;
        }

        var count = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            count++;
            r.Skip();
        }

        return count;
    }

    /// <summary>True for a non-empty object or array; leaves the reader on the value's last token.</summary>
    private static bool HasContent(ref Utf8JsonReader r)
    {
        if (r.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return false;
        }

        var depth = r.CurrentDepth;
        var any = false;
        while (r.Read() && r.CurrentDepth > depth)
        {
            any = true;
            r.Skip();
        }

        return any;
    }

    private static BodyTextEncoding ParseEncoding(string? encoding) => encoding switch
    {
        null or "" => BodyTextEncoding.None,
        _ when encoding.Equals("base64", StringComparison.OrdinalIgnoreCase) => BodyTextEncoding.Base64,
        _ => BodyTextEncoding.Other,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
