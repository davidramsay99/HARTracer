using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace HarLens.Core.Text;

/// <summary>Pretty-printing for the Body "Pretty" view and for compare (SPEC 6.3, 6.6).</summary>
public static class JsonPretty
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool TryFormat(string? text, out string pretty)
    {
        pretty = text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 4096 });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                doc.RootElement.WriteTo(writer);
            }

            pretty = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool LooksLikeJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.AsSpan().Trim();
        return (t[0] == '{' && t[^1] == '}') || (t[0] == '[' && t[^1] == ']');
    }
}

public static class XmlPretty
{
    /// <summary>Indents XML. DTDs are prohibited and no resolver is set, so nothing external is ever fetched (SPEC 3).</summary>
    public static bool TryFormat(string? text, out string pretty)
    {
        pretty = text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreWhitespace = true,
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = !text.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) }))
            {
                writer.WriteNode(reader, true);
            }

            pretty = sb.ToString();
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}

/// <summary>
/// Indents HTML by tag structure without a DOM or a rendering engine (SPEC 3.4: HTML is shown as text only).
/// Script, style, pre and textarea contents are left as written.
/// </summary>
public static partial class HtmlIndenter
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "!doctype",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "pre", "textarea" };

    [GeneratedRegex(@"<!--.*?-->|<(/?)([a-zA-Z!][a-zA-Z0-9:-]*)[^>]*?(/?)>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    public static string Format(string html)
    {
        var sb = new StringBuilder(html.Length + html.Length / 4);
        var depth = 0;
        var position = 0;
        foreach (Match m in TagRegex().Matches(html))
        {
            if (m.Index < position)
            {
                continue;
            }

            AppendText(sb, html[position..m.Index], depth);
            var isComment = m.Value.StartsWith("<!--", StringComparison.Ordinal);
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value;
            var selfClosing = m.Groups[3].Value == "/" || VoidElements.Contains(name) || isComment;
            if (closing)
            {
                depth = Math.Max(0, depth - 1);
            }

            AppendLine(sb, m.Value, depth);
            position = m.Index + m.Length;
            if (!closing && !selfClosing)
            {
                if (RawText.Contains(name))
                {
                    var end = html.IndexOf("</" + name, position, StringComparison.OrdinalIgnoreCase);
                    if (end < 0)
                    {
                        end = html.Length;
                    }

                    var raw = html[position..end];
                    if (raw.Trim().Length > 0)
                    {
                        sb.Append(raw.Trim('\r', '\n')).Append('\n');
                    }

                    position = end;
                }
                else
                {
                    depth++;
                }
            }
        }

        AppendText(sb, html[position..], depth);
        return sb.ToString().TrimEnd();
    }

    private static void AppendText(StringBuilder sb, string text, int depth)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            AppendLine(sb, trimmed, depth);
        }
    }

    private static void AppendLine(StringBuilder sb, string line, int depth) => sb.Append(' ', depth * 2).Append(line).Append('\n');
}

/// <summary>Offset, hex, ASCII rows for the Hex view, produced on demand per row for virtualization.</summary>
public static class HexDump
{
    public const int BytesPerRow = 16;

    public static int RowCount(int length) => (length + BytesPerRow - 1) / BytesPerRow;

    public static string FormatRow(ReadOnlySpan<byte> data, int row)
    {
        var start = row * BytesPerRow;
        var count = Math.Min(BytesPerRow, data.Length - start);
        var sb = new StringBuilder(80);
        sb.Append(start.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
        for (var i = 0; i < BytesPerRow; i++)
        {
            if (i < count)
            {
                sb.Append(data[start + i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
            }
            else
            {
                sb.Append("   ");
            }

            if (i == 7)
            {
                sb.Append(' ');
            }
        }

        sb.Append(" |");
        for (var i = 0; i < count; i++)
        {
            var b = data[start + i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        sb.Append('|');
        return sb.ToString();
    }

    public static string Format(ReadOnlySpan<byte> data, int maxRows = int.MaxValue)
    {
        var sb = new StringBuilder();
        var rows = Math.Min(RowCount(data.Length), maxRows);
        for (var r = 0; r < rows; r++)
        {
            sb.Append(FormatRow(data, r)).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>A JSON Web Token found in a header or body, decoded without signature verification (SPEC 6.3).</summary>
public sealed record JwtToken(string Token, string Location, string HeaderJson, string PayloadJson, string Signature)
{
    public DateTimeOffset? Expires => Claim("exp");

    public DateTimeOffset? IssuedAt => Claim("iat");

    public DateTimeOffset? NotBefore => Claim("nbf");

    private DateTimeOffset? Claim(string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(PayloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

public static partial class Jwt
{
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtRegex();

    /// <summary>Finds and decodes every three-part base64url token in <paramref name="text"/>.</summary>
    public static IEnumerable<JwtToken> FindAll(string? text, string location)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("eyJ", StringComparison.Ordinal))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in JwtRegex().Matches(text))
        {
            if (seen.Add(m.Value) && TryDecode(m.Value, location, out var token))
            {
                yield return token;
            }
        }
    }

    public static bool TryDecode(string token, string location, out JwtToken result)
    {
        result = null!;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var header = DecodeSegment(parts[0]);
        var payload = DecodeSegment(parts[1]);
        if (header is null || payload is null)
        {
            return false;
        }

        result = new JwtToken(token, location,
            JsonPretty.TryFormat(header, out var h) ? h : header,
            JsonPretty.TryFormat(payload, out var p) ? p : payload,
            parts[2]);
        return true;
    }

    public static string? DecodeSegment(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            var bytes = Convert.FromBase64String(s);
            var text = Encoding.UTF8.GetString(bytes);
            return text.TrimStart().StartsWith('{') ? text : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
