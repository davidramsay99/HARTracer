using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Harborer.Core.Har;

/// <summary>Type-tolerant value readers for <see cref="Utf8JsonReader"/>: HAR producers disagree on number and string types.</summary>
internal static class JsonRead
{
    public static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 4096,
    };

    /// <summary>Reads the current token as text. Numbers and booleans become their JSON text; null becomes null.</summary>
    public static string? String(ref Utf8JsonReader r)
    {
        return r.TokenType switch
        {
            JsonTokenType.String => r.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(r.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => SkipAndNull(ref r),
        };
    }

    /// <summary>Reads the current string token through the pool when it is short.</summary>
    public static string? Pooled(ref Utf8JsonReader r, StringPool pool, int maxPooledLength = 256)
    {
        if (r.TokenType != JsonTokenType.String)
        {
            return String(ref r);
        }

        var byteLength = r.ValueSpan.Length;
        if (byteLength == 0)
        {
            return "";
        }

        if (byteLength > maxPooledLength * 3)
        {
            return r.GetString();
        }

        char[]? rented = null;
        Span<char> buffer = byteLength <= 512 ? stackalloc char[byteLength] : (rented = ArrayPool<char>.Shared.Rent(byteLength));
        try
        {
            var written = r.CopyString(buffer);
            var chars = buffer[..written];
            return written > maxPooledLength ? new string(chars) : pool.Get(chars);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    public static double Double(ref Utf8JsonReader r, double fallback = -1)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number:
                return r.TryGetDouble(out var d) ? d : fallback;
            case JsonTokenType.String:
                return double.TryParse(r.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : fallback;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                r.Skip();
                return fallback;
            default:
                return fallback;
        }
    }

    public static long Int64(ref Utf8JsonReader r, long fallback = -1)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number:
                if (r.TryGetInt64(out var l))
                {
                    return l;
                }

                return r.TryGetDouble(out var d) ? (long)d : fallback;
            case JsonTokenType.String:
                return long.TryParse(r.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : fallback;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                r.Skip();
                return fallback;
            default:
                return fallback;
        }
    }

    public static DateTimeOffset Date(ref Utf8JsonReader r)
    {
        if (r.TokenType != JsonTokenType.String)
        {
            r.Skip();
            return DateTimeOffset.MinValue;
        }

        if (r.TryGetDateTimeOffset(out var value))
        {
            return value;
        }

        return ParseDate(r.GetString());
    }

    public static DateTimeOffset ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string? SkipAndNull(ref Utf8JsonReader r)
    {
        r.Skip();
        return null;
    }
}
