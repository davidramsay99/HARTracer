using System.Text;
using HarLens.Core.Http;

namespace HarLens.Core.Curl;

/// <summary>
/// Writes an HTTP/1.1-style request message: request line with the origin-form target, a Host header first when
/// the request has none, the headers, a blank line and the body. Lines end with CRLF. A multipart body is rendered
/// part by part, with a placeholder line for file content.
/// </summary>
internal static class RawHttpWriter
{
    private const string DefaultBoundary = "----HarLensFormBoundary7MA4YWxkTrZu0gW";

    public static ExportResult Write(HttpRequestSpec request, ExportOptions options)
    {
        var warnings = new List<string>();
        var (authority, target) = RequestExporter.SplitUrl(request.Url);
        var headers = RequestExporter.ExportedHeaders(request, options).ToList();
        var sb = new StringBuilder();
        sb.Append(request.Method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
        if (!headers.Any(h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) && authority.Length > 0)
        {
            sb.Append("Host: ").Append(RequestExporter.HostFromAuthority(authority)).Append("\r\n");
        }

        foreach (var header in headers)
        {
            if (header.Value.Contains('\r', StringComparison.Ordinal) || header.Value.Contains('\n', StringComparison.Ordinal))
            {
                warnings.Add($"The value of header '{header.Name}' contains a line break, which is not valid in HTTP.");
            }

            sb.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        var body = request.Body;
        var parts = body.Mode == BodyMode.Multipart ? body.Parts.Where(p => p.Enabled).ToList() : [];
        var boundary = DefaultBoundary;
        if (parts.Count > 0)
        {
            var contentType = headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
            var existing = contentType is null ? null : BoundaryOf(contentType);
            if (existing is not null)
            {
                boundary = existing;
            }
            else if (contentType is null)
            {
                // The request engine adds this header with its own boundary.
                sb.Append("Content-Type: multipart/form-data; boundary=").Append(boundary).Append("\r\n");
            }
        }

        sb.Append("\r\n");

        switch (body.Mode)
        {
            case BodyMode.None:
                break;
            case BodyMode.Multipart:
                if (parts.Count > 0)
                {
                    AppendMultipart(sb, parts, boundary);
                }

                break;
            case BodyMode.BinaryFile:
                if (!string.IsNullOrEmpty(body.FilePath))
                {
                    sb.Append("<contents of ").Append(body.FilePath).Append('>');
                }

                break;
            default:
                if (body.Bytes is not null)
                {
                    if (CurlText.TryDecodeUtf8Text(body.Bytes, out var text))
                    {
                        sb.Append(text);
                    }
                    else
                    {
                        sb.Append('<').Append(body.Bytes.Length).Append(" bytes of binary data>");
                        warnings.Add("The body is binary and is shown as a placeholder.");
                    }
                }
                else if (body.Text is not null)
                {
                    sb.Append(body.Text);
                }

                break;
        }

        var result = new ExportResult { Text = sb.ToString() };
        result.Warnings.AddRange(warnings);
        return result;
    }

    private static string? BoundaryOf(string contentType)
    {
        foreach (var parameter in contentType.Split(';').Skip(1))
        {
            var eq = parameter.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && parameter[..eq].Trim().Equals("boundary", StringComparison.OrdinalIgnoreCase))
            {
                return parameter[(eq + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    private static string DispositionValue(string value) =>
        value.Replace("\"", "%22", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    private static void AppendMultipart(StringBuilder sb, List<MultipartPart> parts, string boundary)
    {
        foreach (var part in parts)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Disposition: form-data; name=\"").Append(DispositionValue(part.Name)).Append('"');
            var fileName = part.FileName ?? (part.FilePath is not null && !part.FileContentAsValue ? Path.GetFileName(part.FilePath) : null);
            if (fileName is not null)
            {
                sb.Append("; filename=\"").Append(DispositionValue(fileName)).Append('"');
            }

            sb.Append("\r\n");
            if (part.ContentType is not null)
            {
                sb.Append("Content-Type: ").Append(part.ContentType).Append("\r\n");
            }

            sb.Append("\r\n");
            if (part.FilePath is not null)
            {
                sb.Append("<contents of ").Append(part.FilePath).Append('>');
            }
            else
            {
                sb.Append(part.Value);
            }

            sb.Append("\r\n");
        }

        sb.Append("--").Append(boundary).Append("--\r\n");
    }
}
