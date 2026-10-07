using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HarLens.Core.Http;
using HarLens.Core.Model;

namespace HarLens.Core.Har;

/// <summary>One WebSocket frame from Chromium's <c>_webSocketMessages</c>.</summary>
public sealed record WebSocketMessage(string Type, double Time, int Opcode, string Data)
{
    public bool IsSend => Type.Equals("send", StringComparison.OrdinalIgnoreCase);

    public string OpcodeName => Opcode switch
    {
        1 => "text",
        2 => "binary",
        8 => "close",
        9 => "ping",
        10 => "pong",
        _ => Opcode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public DateTimeOffset Timestamp => Time > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(Time * 1000))
        : DateTimeOffset.MinValue;
}

/// <summary>
/// The full entry, parsed on demand from its bytes in the source for the inspector tabs. Disposing releases
/// the parsed document. Large bodies are not materialized as strings here; use <see cref="BodyReader"/>.
/// </summary>
public sealed class EntryDetail : IDisposable
{
    private readonly JsonDocument _document;

    private EntryDetail(HarEntry entry, JsonDocument document)
    {
        Entry = entry;
        _document = document;
    }

    public HarEntry Entry { get; }

    public JsonElement Root => _document.RootElement;

    public static EntryDetail Load(HarEntry entry)
    {
        var bytes = entry.Source.ReadBytes(entry.Offset, entry.Length);
        return new EntryDetail(entry, JsonDocument.Parse(bytes, HarScanner.DocumentOptions));
    }

    public JsonElement? Request => Child(Root, "request");

    public JsonElement? Response => Child(Root, "response");

    /// <summary>Decoded query parameters: <c>request.queryString</c> when present, else parsed from the URL.</summary>
    public List<HarHeader> QueryString
    {
        get
        {
            var fromHar = NameValues(Request, "queryString");
            return fromHar.Count > 0 ? fromHar.Select(p => new HarHeader(FormUrlEncoding.Decode(p.Name), FormUrlEncoding.Decode(p.Value))).ToList()
                : FormUrlEncoding.ParseQuery(Entry.Url);
        }
    }

    public List<CookieInfo> RequestCookies
    {
        get
        {
            var fromHeaders = Cookies.ParseRequestCookies(Entry.RequestHeaders);
            if (fromHeaders.Count > 0)
            {
                return fromHeaders;
            }

            return HarCookies(Request);
        }
    }

    public List<CookieInfo> ResponseCookies
    {
        get
        {
            var fromHeaders = Cookies.ParseSetCookies(Entry.ResponseHeaders);
            return fromHeaders.Count > 0 ? fromHeaders : HarCookies(Response);
        }
    }

    public string? PostDataMimeType => Child(Request, "postData") is { } pd ? Str(pd, "mimeType") : null;

    /// <summary><c>postData.params</c> (form fields as parsed by the producer).</summary>
    public List<HarHeader> PostParams => NameValues(Child(Request, "postData"), "params");

    public JsonElement? Initiator => Child(Root, "_initiator") is { ValueKind: not JsonValueKind.Null } i ? i : null;

    public JsonElement? CacheBeforeRequest => Child(Child(Root, "cache"), "beforeRequest");

    public JsonElement? CacheAfterRequest => Child(Child(Root, "cache"), "afterRequest");

    public string? Comment => Str(Root, "comment");

    public List<WebSocketMessage> WebSocketMessages
    {
        get
        {
            var list = new List<WebSocketMessage>();
            if (Child(Root, "_webSocketMessages") is not { ValueKind: JsonValueKind.Array } messages)
            {
                return list;
            }

            foreach (var m in messages.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                list.Add(new WebSocketMessage(
                    Str(m, "type") ?? "",
                    m.TryGetProperty("time", out var t) && t.TryGetDouble(out var td) ? td : 0,
                    m.TryGetProperty("opcode", out var o) && o.TryGetInt32(out var oi) ? oi : 0,
                    Str(m, "data") ?? ""));
            }

            return list;
        }
    }

    /// <summary>
    /// Finds a vendor or standard field for custom columns: tries the entry, then request, then response,
    /// and accepts dotted paths such as <c>response._transferSize</c>.
    /// </summary>
    public string? GetField(string path)
    {
        if (path.Contains('.', StringComparison.Ordinal))
        {
            JsonElement? current = Root;
            foreach (var part in path.Split('.'))
            {
                current = Child(current, part);
                if (current is null)
                {
                    return null;
                }
            }

            return Display(current.Value);
        }

        foreach (var scope in new[] { (JsonElement?)Root, Request, Response })
        {
            if (Child(scope, path) is { } value)
            {
                return Display(value);
            }
        }

        return null;
    }

    /// <summary>Indented JSON of the entry with long strings shortened, for the "Raw HAR entry" tab.</summary>
    public string FormatRawJson(int maxStringChars = 16_384) => FormatJson(Root, maxStringChars);

    public static string FormatJson(JsonElement element, int maxStringChars = int.MaxValue)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   SkipValidation = true,
               }))
        {
            WriteShortened(writer, element, maxStringChars);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose() => _document.Dispose();

    private static void WriteShortened(Utf8JsonWriter writer, JsonElement element, int max)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in element.EnumerateObject())
                {
                    writer.WritePropertyName(p.Name);
                    WriteShortened(writer, p.Value, max);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteShortened(writer, item, max);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var s = element.GetString() ?? "";
                writer.WriteStringValue(s.Length > max ? s[..max] + $"… [{s.Length - max:N0} more characters]" : s);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => value.GetRawText(),
    };

    public static JsonElement? Child(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) ? v : null;

    private static string? Str(JsonElement? parent, string name) =>
        Child(parent, name) is { } v ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        } : null;

    private static List<HarHeader> NameValues(JsonElement? parent, string arrayName)
    {
        var list = new List<HarHeader>();
        if (Child(parent, arrayName) is not { ValueKind: JsonValueKind.Array } array)
        {
            return list;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                list.Add(new HarHeader(Str(item, "name") ?? "", Str(item, "value") ?? ""));
            }
        }

        return list;
    }

    private static List<CookieInfo> HarCookies(JsonElement? parent)
    {
        var list = new List<CookieInfo>();
        if (Child(parent, "cookies") is not { ValueKind: JsonValueKind.Array } array)
        {
            return list;
        }

        foreach (var c in array.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new CookieInfo
            {
                Name = Str(c, "name") ?? "",
                Value = Str(c, "value") ?? "",
                Domain = Str(c, "domain"),
                Path = Str(c, "path"),
                Expires = Str(c, "expires"),
                HttpOnly = Child(c, "httpOnly") is { ValueKind: JsonValueKind.True },
                Secure = Child(c, "secure") is { ValueKind: JsonValueKind.True },
                SameSite = Str(c, "sameSite"),
            });
        }

        return list;
    }
}
