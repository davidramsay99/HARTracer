using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using HarLens.Core.Text;

namespace HarLens.Core.Har;

public enum BodySide
{
    Request,
    Response,
}

/// <summary>A body read from the source and decoded for display (base64 and stored content codings removed).</summary>
public sealed class DecodedBody
{
    public required byte[] Bytes { get; init; }

    /// <summary>The body as text when it is textual; null for binary bodies.</summary>
    public string? Text { get; init; }

    public bool IsText => Text is not null;

    public string MimeType { get; init; } = "";

    public bool WasBase64 { get; init; }

    public List<string> Notices { get; } = [];

    /// <summary>Approximate managed memory held, for the LRU cache.</summary>
    internal long Weight => Bytes.Length + (Text?.Length ?? 0) * 2L + 64;
}

/// <summary>Reads body strings from a <see cref="HarSource"/> by offset and decodes them (SPEC 5.2, 5.3).</summary>
public static class BodyReader
{
    public static DecodedBody? Read(HarEntry entry, BodySide side)
    {
        var bodyRef = side == BodySide.Response ? entry.ResponseBody : entry.RequestBody;
        var mime = side == BodySide.Response ? entry.MimeType : entry.RequestMimeType ?? entry.GetRequestHeader("Content-Type") ?? "";
        if (!bodyRef.Exists)
        {
            return null;
        }

        var raw = entry.Source.ReadBytes(bodyRef.Offset, bodyRef.Length);
        if (bodyRef.Encoding == BodyTextEncoding.Base64)
        {
            var bytes = DecodeBase64Token(raw, out var base64Ok);
            if (!base64Ok)
            {
                var text = UnescapeToken(raw, bodyRef.IsEscaped);
                var invalid = new DecodedBody { Bytes = Encoding.UTF8.GetBytes(text), Text = text, MimeType = mime };
                invalid.Notices.Add("The body is marked base64 but is not valid base64; showing it as stored.");
                return invalid;
            }

            var notices = new List<string>();
            if (side == BodySide.Response)
            {
                var decoded = ContentDecoder.DecodeIfEncoded(bytes, entry.GetResponseHeader("Content-Encoding"), entry.ContentSize);
                bytes = decoded.Bytes;
                if (decoded.Notice is not null)
                {
                    notices.Add(decoded.Notice);
                }
            }

            string? asText = null;
            if (MimeTypes.IsTextual(mime) || (string.IsNullOrEmpty(mime) && LooksLikeText(bytes)))
            {
                asText = TextFromBytes(bytes, mime);
            }

            var body = new DecodedBody { Bytes = bytes, Text = asText, MimeType = mime, WasBase64 = true };
            body.Notices.AddRange(notices);
            return body;
        }

        var plain = UnescapeToken(raw, bodyRef.IsEscaped);
        var result = new DecodedBody { Bytes = Encoding.UTF8.GetBytes(plain), Text = plain, MimeType = mime };
        if (bodyRef.Encoding == BodyTextEncoding.Other)
        {
            result.Notices.Add("The body uses an encoding this application does not recognise; showing it as stored.");
        }

        return result;
    }

    /// <summary>Text of a body for searching: decoded when the body is textual, null for binary (SPEC 6.5).</summary>
    public static string? ReadSearchableText(HarEntry entry, BodySide side)
    {
        var bodyRef = side == BodySide.Response ? entry.ResponseBody : entry.RequestBody;
        if (!bodyRef.Exists)
        {
            return null;
        }

        if (bodyRef.Encoding != BodyTextEncoding.Base64)
        {
            return UnescapeToken(entry.Source.ReadBytes(bodyRef.Offset, bodyRef.Length), bodyRef.IsEscaped);
        }

        var mime = side == BodySide.Response ? entry.MimeType : entry.RequestMimeType;
        return MimeTypes.IsTextual(mime) ? Read(entry, side)?.Text : null;
    }

    internal static string UnescapeToken(byte[] token, bool escaped)
    {
        // token is a JSON string including its quotes.
        if (!escaped && token.Length >= 2)
        {
            return Encoding.UTF8.GetString(token, 1, token.Length - 2);
        }

        var reader = new Utf8JsonReader(token);
        reader.Read();
        return reader.GetString() ?? "";
    }

    private static byte[] DecodeBase64Token(byte[] token, out bool ok)
    {
        ok = true;
        var reader = new Utf8JsonReader(token);
        reader.Read();
        if (!reader.ValueIsEscaped)
        {
            var span = reader.ValueSpan;
            var max = Base64.GetMaxDecodedFromUtf8Length(span.Length);
            var output = new byte[max];
            if (Base64.DecodeFromUtf8(span, output, out _, out var written) == OperationStatus.Done)
            {
                return written == max ? output : output.AsSpan(0, written).ToArray();
            }
        }

        // Escaped (for example "\/") or containing whitespace or line breaks.
        var text = reader.GetString() ?? "";
        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                cleaned.Append(c);
            }
        }

        var s = cleaned.ToString().Replace('-', '+').Replace('_', '/');
        var pad = s.Length % 4;
        if (pad > 0)
        {
            s = s.PadRight(s.Length + (4 - pad), '=');
        }

        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            ok = false;
            return [];
        }
    }

    internal static string TextFromBytes(byte[] bytes, string? mime)
    {
        var charset = MimeTypes.GetParameter(mime, "charset")?.ToLowerInvariant();
        Encoding encoding = charset switch
        {
            "iso-8859-1" or "latin1" or "windows-1252" => Encoding.Latin1,
            "us-ascii" or "ascii" => Encoding.ASCII,
            "utf-16" or "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };

        var span = bytes.AsSpan();
        if (encoding is UTF8Encoding && span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
        }

        return encoding.GetString(span);
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        foreach (var b in sample)
        {
            if (b < 0x09 || (b is > 0x0D and < 0x20))
            {
                return false;
            }
        }

        return System.Text.Unicode.Utf8.IsValid(sample) || sample.Length == 4096;
    }
}

/// <summary>Bounded LRU cache of decoded bodies (SPEC 5.3). Thread-safe.</summary>
public sealed class BodyCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(int Source, long Offset, BodySide Side), LinkedListNode<Item>> _map = [];
    private readonly LinkedList<Item> _lru = [];
    private long _weight;

    public BodyCache(long capacityBytes = 256L * 1024 * 1024)
    {
        CapacityBytes = capacityBytes;
    }

    public long CapacityBytes { get; }

    public long CurrentBytes
    {
        get
        {
            lock (_lock)
            {
                return _weight;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    public DecodedBody? Get(HarEntry entry, BodySide side)
    {
        var key = (entry.Source.Id, entry.Offset, side);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Body;
            }
        }

        var body = BodyReader.Read(entry, side);
        if (body is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                return existing.Value.Body;
            }

            if (body.Weight > CapacityBytes)
            {
                return body;
            }

            var node = _lru.AddFirst(new Item(key, body));
            _map[key] = node;
            _weight += body.Weight;
            while (_weight > CapacityBytes && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _weight -= last.Value.Body.Weight;
            }
        }

        return body;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _weight = 0;
        }
    }

    private sealed record Item((int Source, long Offset, BodySide Side) Key, DecodedBody Body);
}
