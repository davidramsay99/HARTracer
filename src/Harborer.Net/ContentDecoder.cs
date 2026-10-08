using System.Globalization;
using System.IO.Compression;

namespace Harborer.Net;

internal sealed record DecodeResult(byte[] Body, bool Decoded, bool Truncated, List<string> Notices);

/// <summary>
/// Decodes a response body according to Content-Encoding with System.IO.Compression. Done after the raw
/// bytes are read, instead of AutomaticDecompression, so the header and the wire size stay visible.
/// </summary>
internal static class ContentDecoder
{
    public static List<string> ParseEncodings(IEnumerable<string> headerValues) =>
        headerValues
            .SelectMany(v => v.Split(','))
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0 && t != "identity")
            .ToList();

    /// <param name="raw">Body as received.</param>
    /// <param name="encodings">Codings in the order they were applied (header order); decoded in reverse.</param>
    /// <param name="cap">Maximum decoded size.</param>
    /// <param name="sourceIncomplete">True when the raw body was cut short (size cap, error or cancel).</param>
    public static DecodeResult Decode(byte[] raw, IReadOnlyList<string> encodings, long cap, bool sourceIncomplete)
    {
        var notices = new List<string>();
        if (encodings.Count == 0 || raw.Length == 0)
        {
            return new DecodeResult(raw, false, false, notices);
        }

        foreach (string encoding in encodings)
        {
            if (!IsSupported(encoding))
            {
                notices.Add(encoding == "zstd"
                    ? "Zstandard bodies are displayed as undecoded bytes"
                    : $"Content-Encoding \"{encoding}\" is not supported; the body is displayed as undecoded bytes");
                return new DecodeResult(raw, false, false, notices);
            }
        }

        byte[] data = raw;
        bool truncated = false;
        for (int i = encodings.Count - 1; i >= 0; i--)
        {
            try
            {
                (data, bool capped) = DecodeOne(data, encodings[i], cap, sourceIncomplete);
                truncated |= capped;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                notices.Add($"The {encodings[i]} body could not be decoded ({ex.Message}); it is displayed as received");
                return new DecodeResult(raw, false, false, notices);
            }
        }

        if (truncated)
        {
            notices.Add(string.Create(CultureInfo.InvariantCulture, $"Decoded body truncated at {cap} bytes (response size cap)"));
        }

        if (sourceIncomplete)
        {
            notices.Add("The body was decoded from incomplete data, so the decoded content is partial");
        }

        return new DecodeResult(data, true, truncated, notices);
    }

    private static bool IsSupported(string encoding) => encoding is "gzip" or "x-gzip" or "deflate" or "br";

    private static (byte[] Data, bool Capped) DecodeOne(byte[] input, string encoding, long cap, bool allowPartial)
    {
        if (encoding == "deflate" && LooksLikeZlib(input))
        {
            try
            {
                return Run(new ZLibStream(new MemoryStream(input), CompressionMode.Decompress), cap, allowPartial);
            }
            catch (InvalidDataException)
            {
                // Some servers send raw deflate with a lucky first byte pair; try that below.
            }
        }

        Stream decoder = encoding switch
        {
            "gzip" or "x-gzip" => new GZipStream(new MemoryStream(input), CompressionMode.Decompress),
            "br" => new BrotliStream(new MemoryStream(input), CompressionMode.Decompress),
            _ => new DeflateStream(new MemoryStream(input), CompressionMode.Decompress),
        };
        return Run(decoder, cap, allowPartial);
    }

    private static (byte[] Data, bool Capped) Run(Stream decoder, long cap, bool allowPartial)
    {
        using (decoder)
        {
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            bool capped = false;
            try
            {
                while (true)
                {
                    int read = decoder.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    long room = cap - output.Length;
                    if (read > room)
                    {
                        output.Write(buffer, 0, (int)room);
                        capped = true;
                        break;
                    }

                    output.Write(buffer, 0, read);
                    if (output.Length == cap)
                    {
                        capped = decoder.Read(buffer, 0, 1) > 0;
                        break;
                    }
                }
            }
            catch (Exception ex) when ((ex is InvalidDataException or IOException) && allowPartial && output.Length > 0)
            {
                // A body cut short by the size cap ends mid-stream; keep what decoded.
            }

            return (output.ToArray(), capped);
        }
    }

    // RFC 1950 header: CM = 8 and the 16-bit header is a multiple of 31.
    private static bool LooksLikeZlib(byte[] data) =>
        data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
}
