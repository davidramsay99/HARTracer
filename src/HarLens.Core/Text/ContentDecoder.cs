using System.IO.Compression;

namespace HarLens.Core.Text;

/// <summary>
/// Decodes HTTP content codings with System.IO.Compression: gzip, deflate (zlib or raw) and brotli.
/// Zstandard has no decoder in the base library and is reported, not decoded.
/// </summary>
public static class ContentDecoder
{
    public sealed record Result(byte[] Bytes, bool Decoded, string? Notice);

    public static IReadOnlyList<string> ParseCodings(string? contentEncoding) =>
        string.IsNullOrWhiteSpace(contentEncoding)
            ? []
            : contentEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToLowerInvariant())
                .Where(c => c is not "identity")
                .ToList();

    /// <summary>
    /// Decodes <paramref name="bytes"/> when they still carry the content coding. A body whose length already equals
    /// the expected decoded size is left alone, which is how browsers store bodies.
    /// </summary>
    public static Result DecodeIfEncoded(byte[] bytes, string? contentEncoding, long expectedDecodedSize, long maxOutput = 512L * 1024 * 1024)
    {
        var codings = ParseCodings(contentEncoding);
        if (codings.Count == 0 || bytes.Length == 0)
        {
            return new Result(bytes, false, null);
        }

        if (expectedDecodedSize >= 0 && bytes.Length == expectedDecodedSize && !LooksCompressed(bytes, codings[^1]))
        {
            return new Result(bytes, false, null);
        }

        var current = bytes;
        for (var i = codings.Count - 1; i >= 0; i--)
        {
            var coding = codings[i];
            if (coding is "zstd")
            {
                return new Result(current, false, "Zstandard (zstd) content is shown as undecoded bytes: no decoder is available in .NET.");
            }

            var decoded = TryDecode(current, coding, maxOutput);
            if (decoded is null)
            {
                return i == codings.Count - 1 && expectedDecodedSize < 0
                    ? new Result(bytes, false, null)
                    : new Result(current, false, $"Content-Encoding '{coding}' could not be decoded; showing the stored bytes.");
            }

            current = decoded;
        }

        return new Result(current, true, $"Body was stored with Content-Encoding '{contentEncoding}'; showing decoded content.");
    }

    public static byte[]? TryDecode(byte[] input, string coding, long maxOutput)
    {
        try
        {
            switch (coding)
            {
                case "gzip" or "x-gzip":
                    if (input.Length < 2 || input[0] != 0x1F || input[1] != 0x8B)
                    {
                        return null;
                    }

                    return Inflate(new GZipStream(new MemoryStream(input), CompressionMode.Decompress), maxOutput);
                case "deflate":
                    // RFC 9110 "deflate" is zlib-wrapped; some servers send raw deflate.
                    return LooksZlib(input)
                        ? Inflate(new ZLibStream(new MemoryStream(input), CompressionMode.Decompress), maxOutput)
                        : Inflate(new DeflateStream(new MemoryStream(input), CompressionMode.Decompress), maxOutput);
                case "br":
                    return Inflate(new BrotliStream(new MemoryStream(input), CompressionMode.Decompress), maxOutput);
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool LooksCompressed(byte[] bytes, string coding) => coding switch
    {
        "gzip" or "x-gzip" => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B,
        "deflate" => LooksZlib(bytes),
        "zstd" => bytes.Length >= 4 && bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD,
        _ => false,
    };

    private static bool LooksZlib(byte[] b) => b.Length >= 2 && (b[0] & 0x0F) == 8 && ((b[0] << 8) | b[1]) % 31 == 0;

    private static byte[] Inflate(Stream decoder, long maxOutput)
    {
        using (decoder)
        {
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > maxOutput)
                {
                    break;
                }
            }

            return output.ToArray();
        }
    }
}
