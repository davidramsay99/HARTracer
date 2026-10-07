using System.Text;
using System.Text.Unicode;
using HarLens.Core.Model;

namespace HarLens.Net;

/// <summary>An HTTP/1.x message head as it appeared on the wire.</summary>
internal sealed record WireHead(string StartLine, List<HarHeader> Headers, int Length)
{
    /// <summary>Status code of a response head, 0 when the start line is not a status line.</summary>
    public int StatusCode
    {
        get
        {
            string[] parts = StartLine.Split(' ', 3);
            return parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.Ordinal) &&
                int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int code)
                ? code
                : 0;
        }
    }

    public string ReasonPhrase
    {
        get
        {
            string[] parts = StartLine.Split(' ', 3);
            return parts.Length == 3 ? parts[2] : "";
        }
    }
}

/// <summary>
/// Parses HTTP/1.x heads out of recorded wire bytes so header names, casing, order and duplicates are exactly as
/// sent and received. Returns null when the head is incomplete (for example cut by the recording cap).
/// </summary>
internal static class WireMessageParser
{
    public static WireHead? ParseRequestHead(ReadOnlySpan<byte> wire)
    {
        var head = ParseHead(wire, 0);
        if (head is null)
        {
            return null;
        }

        // "METHOD target HTTP/x.y"
        string[] parts = head.StartLine.Split(' ');
        return parts.Length == 3 && parts[2].StartsWith("HTTP/", StringComparison.Ordinal) ? head : null;
    }

    /// <summary>The final response head, skipping interim 1xx responses such as 100 Continue.</summary>
    public static WireHead? ParseFinalResponseHead(ReadOnlySpan<byte> wire)
    {
        int offset = 0;
        while (true)
        {
            var head = ParseHead(wire, offset);
            if (head is null || head.StatusCode == 0)
            {
                return null;
            }

            if (head.StatusCode is >= 100 and < 200 and not 101)
            {
                offset += head.Length;
                continue;
            }

            return head;
        }
    }

    internal static WireHead? ParseHead(ReadOnlySpan<byte> wire, int offset)
    {
        var headers = new List<HarHeader>();
        string? startLine = null;
        int position = offset;
        while (true)
        {
            int newline = wire[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                return null;
            }

            int lineEnd = position + newline;
            int contentEnd = lineEnd > position && wire[lineEnd - 1] == (byte)'\r' ? lineEnd - 1 : lineEnd;
            var line = wire[position..contentEnd];
            position = lineEnd + 1;

            if (startLine is null)
            {
                // RFC 9112 allows empty lines before the start line.
                if (line.IsEmpty)
                {
                    continue;
                }

                startLine = Decode(line);
                continue;
            }

            if (line.IsEmpty)
            {
                return new WireHead(startLine, headers, position - offset);
            }

            if ((line[0] == (byte)' ' || line[0] == (byte)'\t') && headers.Count > 0)
            {
                // Obsolete line folding: continue the previous value.
                var previous = headers[^1];
                headers[^1] = previous with { Value = previous.Value + " " + Decode(line).Trim() };
                continue;
            }

            int colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                headers.Add(new HarHeader(Decode(line).Trim(), ""));
                continue;
            }

            headers.Add(new HarHeader(Decode(line[..colon]), Decode(line[(colon + 1)..]).Trim(' ', '\t')));
        }
    }

    // UTF-8 when valid (what the engine sends), Latin-1 otherwise so every byte stays visible.
    private static string Decode(ReadOnlySpan<byte> bytes) =>
        Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
}
