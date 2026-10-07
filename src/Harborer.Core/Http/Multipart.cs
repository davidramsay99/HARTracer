using System.Text;
using Harborer.Core.Model;
using Harborer.Core.Text;

namespace Harborer.Core.Http;

/// <summary>One part of a captured multipart/form-data body, for the Pretty parts grid.</summary>
public sealed class MultipartSection
{
    public List<HarHeader> Headers { get; } = [];

    public string? Name { get; set; }

    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public byte[] Body { get; set; } = [];

    public string Preview
    {
        get
        {
            if (FileName is not null && !MimeTypes.IsTextual(ContentType))
            {
                return $"[{Body.Length:N0} bytes]";
            }

            var text = Encoding.UTF8.GetString(Body);
            return text.Length > 500 ? text[..500] + "…" : text;
        }
    }
}

public static class MultipartParser
{
    /// <summary>Splits a multipart body on the boundary from its Content-Type. Returns an empty list when it cannot.</summary>
    public static List<MultipartSection> Parse(byte[] body, string? contentType)
    {
        var sections = new List<MultipartSection>();
        var boundary = MimeTypes.GetParameter(contentType, "boundary");
        if (string.IsNullOrEmpty(boundary))
        {
            // Browsers sometimes omit the header in HAR postData; infer the boundary from the first line.
            var firstLineEnd = body.AsSpan().IndexOf("\r\n"u8);
            if (firstLineEnd > 2 && body[0] == '-' && body[1] == '-')
            {
                boundary = Encoding.ASCII.GetString(body, 2, firstLineEnd - 2);
            }
            else
            {
                return sections;
            }
        }

        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var span = body.AsSpan();
        var index = span.IndexOf(delimiter);
        while (index >= 0)
        {
            var afterDelimiter = index + delimiter.Length;
            if (afterDelimiter + 2 <= span.Length && span[afterDelimiter] == '-' && span[afterDelimiter + 1] == '-')
            {
                break; // closing delimiter
            }

            var partStart = SkipLineBreak(span, afterDelimiter);
            var next = span[partStart..].IndexOf(delimiter);
            var partEnd = next < 0 ? span.Length : partStart + next;
            sections.Add(ParseSection(span[partStart..TrimTrailingBreak(span, partStart, partEnd)]));
            index = next < 0 ? -1 : partEnd;
        }

        return sections;
    }

    private static MultipartSection ParseSection(ReadOnlySpan<byte> part)
    {
        var section = new MultipartSection();
        var headerEnd = part.IndexOf("\r\n\r\n"u8);
        var separatorLength = 4;
        if (headerEnd < 0)
        {
            headerEnd = part.IndexOf("\n\n"u8);
            separatorLength = 2;
        }

        if (headerEnd < 0)
        {
            section.Body = part.ToArray();
            return section;
        }

        var headerText = Encoding.UTF8.GetString(part[..headerEnd]);
        foreach (var line in headerText.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            section.Headers.Add(new HarHeader(name, value));
            if (name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                section.Name = DispositionParameter(value, "name");
                section.FileName = DispositionParameter(value, "filename");
            }
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                section.ContentType = value;
            }
        }

        section.Body = part[(headerEnd + separatorLength)..].ToArray();
        return section;
    }

    private static string? DispositionParameter(string value, string name)
    {
        foreach (var piece in value.Split(';').Skip(1))
        {
            var eq = piece.IndexOf('=');
            if (eq > 0 && piece[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return piece[(eq + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    private static int SkipLineBreak(ReadOnlySpan<byte> span, int i)
    {
        if (i < span.Length && span[i] == '\r')
        {
            i++;
        }

        if (i < span.Length && span[i] == '\n')
        {
            i++;
        }

        return i;
    }

    private static int TrimTrailingBreak(ReadOnlySpan<byte> span, int start, int end)
    {
        if (end - 1 >= start && span[end - 1] == '\n')
        {
            end--;
        }

        if (end - 1 >= start && span[end - 1] == '\r')
        {
            end--;
        }

        return end;
    }
}
