using System.Security.Cryptography;
using System.Text;
using HarLens.Core.Engine;
using HarLens.Core.Http;

namespace HarLens.Net;

/// <summary>The request body, read once before the first connection so file errors surface early and redirects can resend it.</summary>
internal sealed class PreparedBody
{
    public PreparedBody(byte[] bytes, string? contentType = null, bool replacesUserContentType = false)
    {
        Bytes = bytes;
        ContentType = contentType;
        ReplacesUserContentType = replacesUserContentType;
    }

    public byte[] Bytes { get; }

    /// <summary>Content-Type to send when the composed headers have none.</summary>
    public string? ContentType { get; }

    /// <summary>
    /// True when the composed multipart Content-Type had no boundary: <see cref="ContentType"/> then holds that value
    /// with the generated boundary appended, and replaces it.
    /// </summary>
    public bool ReplacesUserContentType { get; }
}

internal static class BodyBuilder
{
    public static PreparedBody? Prepare(HttpRequestSpec request, SendSettings settings)
    {
        var body = request.Body;
        switch (body.Mode)
        {
            case BodyMode.Raw:
            case BodyMode.Json:
            case BodyMode.FormUrlEncoded:
                byte[]? bytes = body.Bytes ?? (body.Text is null ? null : Encoding.UTF8.GetBytes(body.Text));
                if (bytes is null)
                {
                    return null;
                }

                // Like curl -d and --json, a typed body gets a matching Content-Type unless one was composed.
                string? defaultType = body.Mode switch
                {
                    BodyMode.Json => "application/json",
                    BodyMode.FormUrlEncoded => "application/x-www-form-urlencoded",
                    _ => null,
                };
                return new PreparedBody(bytes, defaultType);

            case BodyMode.BinaryFile:
                return string.IsNullOrWhiteSpace(body.FilePath)
                    ? null
                    : new PreparedBody(Preflight.ReadFile(body.FilePath, settings.FileBaseDirectory, "the request body"));

            case BodyMode.Multipart:
                return MultipartBuilder.Build(body.Parts, request.GetHeader("Content-Type"), settings.FileBaseDirectory);

            default:
                return null;
        }
    }
}

/// <summary>Builds multipart/form-data by hand for exact control of the boundary and part headers (curl -F layout).</summary>
internal static class MultipartBuilder
{
    private const string BoundaryAlphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // curl's extension table for file parts without an explicit type.
    private static readonly Dictionary<string, string> s_typesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".gif"] = "image/gif",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".htm"] = "text/html",
        [".html"] = "text/html",
        [".pdf"] = "application/pdf",
        [".xml"] = "application/xml",
    };

    public static PreparedBody? Build(IEnumerable<MultipartPart> parts, string? userContentType, string? baseDirectory)
    {
        var enabled = parts.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return null;
        }

        string boundary;
        string? contentType = null;
        bool replace = false;
        if (userContentType is not null && TryGetBoundary(userContentType, out var existing))
        {
            boundary = existing;
        }
        else
        {
            boundary = NewBoundary();
            if (userContentType is not null)
            {
                // curl appends the boundary to a custom multipart type.
                contentType = userContentType.Trim().TrimEnd(';').TrimEnd() + "; boundary=" + boundary;
                replace = true;
            }
            else
            {
                contentType = "multipart/form-data; boundary=" + boundary;
            }
        }

        using var output = new MemoryStream();
        foreach (var part in enabled)
        {
            WritePart(output, boundary, part, baseDirectory);
        }

        WriteText(output, "--" + boundary + "--\r\n");
        return new PreparedBody(output.ToArray(), contentType, replace);
    }

    internal static bool TryGetBoundary(string contentType, out string boundary)
    {
        foreach (string parameter in contentType.Split(';').Skip(1))
        {
            string trimmed = parameter.Trim();
            int equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !trimmed[..equals].Trim().Equals("boundary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = trimmed[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
            }

            if (value.Length > 0)
            {
                boundary = value;
                return true;
            }
        }

        boundary = "";
        return false;
    }

    internal static string NewBoundary() =>
        new string('-', 24) + RandomNumberGenerator.GetString(BoundaryAlphabet, 22);

    internal static string GuessContentType(string fileName) =>
        s_typesByExtension.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    private static void WritePart(MemoryStream output, string boundary, MultipartPart part, string? baseDirectory)
    {
        byte[] data;
        string? fileName;
        string? type = part.ContentType;
        if (part.IsFile)
        {
            data = Preflight.ReadFile(part.FilePath, baseDirectory, $"multipart part '{part.Name}'");
            if (part.FileContentAsValue)
            {
                // curl -F name=<file: the file's content as a plain field.
                fileName = part.FileName;
            }
            else
            {
                fileName = part.FileName ?? Path.GetFileName(part.FilePath!);
                type ??= GuessContentType(fileName);
            }
        }
        else
        {
            data = Encoding.UTF8.GetBytes(part.Value ?? "");
            fileName = part.FileName;
        }

        var head = new StringBuilder();
        head.Append("--").Append(boundary).Append("\r\n");
        head.Append("Content-Disposition: form-data; name=\"").Append(Escape(part.Name)).Append('"');
        if (fileName is not null)
        {
            head.Append("; filename=\"").Append(Escape(fileName)).Append('"');
        }

        head.Append("\r\n");
        if (!string.IsNullOrEmpty(type))
        {
            head.Append("Content-Type: ").Append(type).Append("\r\n");
        }

        head.Append("\r\n");
        WriteText(output, head.ToString());
        output.Write(data);
        WriteText(output, "\r\n");
    }

    // Percent-escaping of quotes and line breaks in names, as curl does by default (HTML5 form encoding).
    private static string Escape(string value) => value
        .Replace("\"", "%22", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

    private static void WriteText(MemoryStream output, string text) => output.Write(Encoding.UTF8.GetBytes(text));
}
