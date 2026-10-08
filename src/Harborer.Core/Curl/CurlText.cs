using System.Globalization;
using System.Text;

namespace Harborer.Core.Curl;

/// <summary>Collects warnings in order and drops exact duplicates so a repeated construct is reported once.</summary>
internal sealed class WarningSink
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public List<string> Items { get; } = [];

    public void Add(string message)
    {
        if (_seen.Add(message))
        {
            Items.Add(message);
        }
    }
}

/// <summary>One argument as produced by a shell lexer.</summary>
/// <param name="Text">The argument after quote removal.</param>
/// <param name="AfterLineBreak">True when an unescaped line break separates this argument from the previous one.</param>
internal readonly record struct ShellWord(string Text, bool AfterLineBreak);

/// <summary>
/// Accumulates one shell word. Characters are appended as text; bytes produced by escapes such as
/// bash <c>\xHH</c> are buffered and decoded with <see cref="CurlText.DecodeUtf8Lenient(IReadOnlyList{byte}, StringBuilder)"/>.
/// </summary>
internal sealed class WordBuilder
{
    private readonly StringBuilder _text = new();
    private readonly List<byte> _bytes = [];

    /// <summary>True once anything, including an empty quoted string, has been seen for the current word.</summary>
    public bool Started { get; private set; }

    public void Start() => Started = true;

    public void Append(char c)
    {
        Flush();
        _text.Append(c);
        Started = true;
    }

    public void Append(string s)
    {
        Flush();
        _text.Append(s);
        Started = true;
    }

    public void AppendByte(byte b)
    {
        _bytes.Add(b);
        Started = true;
    }

    /// <summary>Appends a Unicode code point. Lone surrogates are kept as UTF-16 code units so escaped pairs combine.</summary>
    public void AppendCodePoint(int codePoint)
    {
        Flush();
        Started = true;
        if (codePoint is >= 0xD800 and <= 0xDFFF)
        {
            _text.Append((char)codePoint);
        }
        else if (codePoint is >= 0 and <= 0x10FFFF)
        {
            _text.Append(char.ConvertFromUtf32(codePoint));
        }
        else
        {
            _text.Append((char)0xFFFD);
        }
    }

    public string Take()
    {
        Flush();
        var s = _text.ToString();
        _text.Clear();
        Started = false;
        return s;
    }

    private void Flush()
    {
        if (_bytes.Count > 0)
        {
            CurlText.DecodeUtf8Lenient(_bytes, _text);
            _bytes.Clear();
        }
    }
}

internal static class CurlText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes UTF-8. A byte that does not start a valid UTF-8 sequence is taken as the Latin-1 character with the
    /// same value. Firefox writes U+0080 to U+00FF as <c>\xHH</c> in <c>$'...'</c> strings, so this recovers the text
    /// it meant, while correct UTF-8 byte escapes (<c>\xc3\xa9</c>) decode normally.
    /// </summary>
    public static void DecodeUtf8Lenient(IReadOnlyList<byte> bytes, StringBuilder sb)
    {
        var i = 0;
        while (i < bytes.Count)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                sb.Append((char)b);
                i++;
                continue;
            }

            var length = b switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0,
            };
            if (length > 0 && i + length <= bytes.Count && TryDecodeSequence(bytes, i, length, out var codePoint))
            {
                sb.Append(char.ConvertFromUtf32(codePoint));
                i += length;
            }
            else
            {
                sb.Append((char)b);
                i++;
            }
        }
    }

    public static string DecodeUtf8Lenient(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        DecodeUtf8Lenient(bytes, sb);
        return sb.ToString();
    }

    private static bool TryDecodeSequence(IReadOnlyList<byte> bytes, int start, int length, out int codePoint)
    {
        codePoint = bytes[start] & (length switch { 2 => 0x1F, 3 => 0x0F, _ => 0x07 });
        for (var k = 1; k < length; k++)
        {
            var c = bytes[start + k];
            if ((c & 0xC0) != 0x80)
            {
                return false;
            }

            codePoint = (codePoint << 6) | (c & 0x3F);
        }

        var min = length switch { 2 => 0x80, 3 => 0x800, _ => 0x10000 };
        return codePoint >= min && codePoint <= 0x10FFFF && codePoint is < 0xD800 or > 0xDFFF;
    }

    /// <summary>True when the bytes are valid UTF-8 (no BOM handling) and contain no NUL.</summary>
    public static bool TryDecodeUtf8Text(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return text.IndexOf('\0') < 0;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    private static bool IsUnreserved(byte b) =>
        b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9')
            or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    /// <summary>
    /// Encoding used by curl's <c>--data-urlencode</c>: unreserved characters are kept, space becomes <c>+</c>,
    /// everything else is <c>%XX</c> over the UTF-8 bytes (verified against curl 8.5.0).
    /// </summary>
    public static string FormUrlEncode(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (IsUnreserved(b))
            {
                sb.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary><c>curl_easy_escape</c>: unreserved characters are kept, everything else is <c>%XX</c>.</summary>
    public static string PercentEncode(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (IsUnreserved(b))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>Escapes text for use inside a JSON string, as curl's <c>{{name:json}}</c> function does.</summary>
    public static string JsonEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    public static bool IsHexDigit(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    public static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10,
    };

    /// <summary>Seconds as curl writes them for <c>-m</c>: invariant culture, no exponent, exact to the tick.</summary>
    public static string FormatSeconds(TimeSpan value) =>
        (value.Ticks / (decimal)TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);

    public static bool TryParseSeconds(string text, out TimeSpan value)
    {
        value = default;
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
        {
            return false;
        }

        var ticks = decimal.Round(seconds * TimeSpan.TicksPerSecond);
        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            return false;
        }

        value = TimeSpan.FromTicks((long)ticks);
        return true;
    }
}
