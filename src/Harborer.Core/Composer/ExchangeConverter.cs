using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harborer.Core.Engine;
using Harborer.Core.Har;
using Harborer.Core.Http;
using Harborer.Core.Model;
using Harborer.Core.Text;

namespace Harborer.Core.Composer;

/// <summary>
/// Turns what the request engine captured into HAR 1.2 entries for the "Composer" session.
/// TLS details, wire bytes and notices go in the <c>_harborer</c> vendor object.
/// </summary>
public static class ExchangeConverter
{
    /// <summary>Wire bytes larger than this are not embedded in the saved entry.</summary>
    public const int MaxEmbeddedWireBytes = 4 * 1024 * 1024;

    public static JsonObject ToHarEntry(ExchangeRecord x, string? originKey = null)
    {
        var requestContentType = Find(x.RequestHeaders, "Content-Type");
        var responseContentType = Find(x.ResponseHeaders, "Content-Type") ?? "";
        var request = new JsonObject
        {
            ["method"] = x.Method,
            ["url"] = x.Url,
            ["httpVersion"] = HarVersion(x.HttpVersion),
            ["headers"] = Headers(x.RequestHeaders),
            ["queryString"] = Pairs(FormUrlEncoding.ParseQuery(x.Url)),
            ["cookies"] = Cookies(Http.Cookies.ParseRequestCookies(x.RequestHeaders)),
            ["headersSize"] = HeaderBlockSize(x.WireSent, x.HttpVersion),
            ["bodySize"] = x.RequestBody?.Length ?? 0,
        };
        if (x.RequestBody is { Length: > 0 } requestBody)
        {
            var postData = new JsonObject { ["mimeType"] = requestContentType ?? "" };
            AddText(postData, requestBody, requestContentType);
            request["postData"] = postData;
        }

        var content = new JsonObject
        {
            ["size"] = x.ResponseBody.Length,
            ["mimeType"] = responseContentType,
        };
        if (x.ResponseBodyWireSize >= 0 && x.ResponseBodyDecompressed)
        {
            content["compression"] = x.ResponseBody.Length - x.ResponseBodyWireSize;
        }

        if (x.ResponseBody.Length > 0)
        {
            AddText(content, x.ResponseBody, responseContentType);
        }

        var response = new JsonObject
        {
            ["status"] = x.Status,
            ["statusText"] = x.StatusText,
            ["httpVersion"] = x.Status == 0 ? "" : HarVersion(x.HttpVersion),
            ["headers"] = Headers(x.ResponseHeaders),
            ["cookies"] = Cookies(Http.Cookies.ParseSetCookies(x.ResponseHeaders)),
            ["content"] = content,
            ["redirectURL"] = x.RedirectLocation ?? Find(x.ResponseHeaders, "Location") ?? "",
            ["headersSize"] = x.Status == 0 ? -1 : HeaderBlockSize(x.WireReceived, x.HttpVersion),
            ["bodySize"] = x.ResponseBodyWireSize,
        };
        if (x.Error is not null)
        {
            response["_error"] = x.Error;
        }

        var t = x.Timings;
        var entry = new JsonObject
        {
            ["startedDateTime"] = x.StartedDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["time"] = Math.Round(Math.Max(0, t.Total), 3),
            ["request"] = request,
            ["response"] = response,
            ["cache"] = new JsonObject(),
            ["timings"] = new JsonObject
            {
                ["blocked"] = Round(t.Blocked),
                ["dns"] = Round(t.Dns),
                ["connect"] = Round(t.Connect),
                ["ssl"] = Round(t.Ssl),
                ["send"] = Math.Max(0, Round(t.Send)),
                ["wait"] = Math.Max(0, Round(t.Wait)),
                ["receive"] = Math.Max(0, Round(t.Receive)),
            },
        };
        if (x.RemoteAddress is not null)
        {
            entry["serverIPAddress"] = x.RemoteAddress;
            entry["connection"] = x.RemotePort.ToString(CultureInfo.InvariantCulture);
        }

        var vendor = new JsonObject { ["composer"] = true };
        if (originKey is not null)
        {
            vendor["origin"] = originKey;
        }

        if (x.Tls is { } tls)
        {
            vendor["tls"] = new JsonObject
            {
                ["protocol"] = tls.Protocol,
                ["cipherSuite"] = tls.CipherSuite,
                ["alpn"] = tls.ApplicationProtocol,
                ["serverName"] = tls.ServerName,
                ["subject"] = tls.Subject,
                ["issuer"] = tls.Issuer,
                ["notBefore"] = tls.NotBefore.ToString("o", CultureInfo.InvariantCulture),
                ["notAfter"] = tls.NotAfter.ToString("o", CultureInfo.InvariantCulture),
                ["subjectAlternativeNames"] = new JsonArray(tls.SubjectAlternativeNames.Select(s => (JsonNode)s).ToArray()),
                ["thumbprint"] = tls.Thumbprint,
                ["policyErrors"] = tls.PolicyErrors,
            };
        }

        if (x.Notices.Count > 0)
        {
            vendor["notices"] = new JsonArray(x.Notices.Select(n => (JsonNode)n).ToArray());
        }

        if (x.ResponseBodyTruncated)
        {
            vendor["bodyTruncated"] = true;
        }

        if (x.WireSent.Length + x.WireReceived.Length > 0 && x.WireSent.Length <= MaxEmbeddedWireBytes && x.WireReceived.Length <= MaxEmbeddedWireBytes)
        {
            vendor["wire"] = new JsonObject
            {
                ["sent"] = Convert.ToBase64String(x.WireSent),
                ["received"] = Convert.ToBase64String(x.WireReceived),
                ["truncated"] = x.WireTruncated,
            };
        }

        entry["_harborer"] = vendor;
        if (x.Error is not null)
        {
            entry["comment"] = x.Error;
        }

        return entry;
    }

    /// <summary>Appends exchanges to a composer session and returns the new index rows.</summary>
    public static List<HarEntry> Append(HarSession composer, IEnumerable<ExchangeRecord> exchanges, string? originKey = null)
    {
        var source = composer.ComposerSource;
        var added = new List<HarEntry>();
        var indexer = new EntryIndexer();
        foreach (var exchange in exchanges)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ToHarEntry(exchange, originKey), HarWriter.NodeOptions);
            var offset = source.Append(bytes);
            var fileIndex = composer.Entries.Count + added.Count;
            var entry = indexer.Index(source, bytes, offset, fileIndex);
            composer.Documents[0].Entries.Add(entry);
            added.Add(entry);
        }

        composer.Append(added);
        return added;
    }

    /// <summary>The exact wire bytes recorded for a composer entry, when present.</summary>
    public static (byte[] Sent, byte[] Received)? ReadWire(HarEntry entry)
    {
        using var detail = EntryDetail.Load(entry);
        var wire = EntryDetail.Child(EntryDetail.Child(detail.Root, "_harborer"), "wire");
        if (wire is null)
        {
            return null;
        }

        var sent = EntryDetail.Child(wire, "sent")?.GetString();
        var received = EntryDetail.Child(wire, "received")?.GetString();
        return sent is null || received is null ? null : (Convert.FromBase64String(sent), Convert.FromBase64String(received));
    }

    private static void AddText(JsonObject holder, byte[] body, string? contentType)
    {
        var textual = MimeTypes.IsTextual(contentType) || (string.IsNullOrEmpty(contentType) && System.Text.Unicode.Utf8.IsValid(body) && !body.Contains((byte)0));
        if (textual && System.Text.Unicode.Utf8.IsValid(body))
        {
            holder["text"] = Encoding.UTF8.GetString(body);
        }
        else
        {
            holder["text"] = Convert.ToBase64String(body);
            holder["encoding"] = "base64";
        }
    }

    private static string HarVersion(string version) => version.ToUpperInvariant() switch
    {
        "HTTP/2" or "HTTP/2.0" => "HTTP/2.0",
        "HTTP/3" or "HTTP/3.0" => "HTTP/3.0",
        "HTTP/1.0" => "HTTP/1.0",
        _ => "HTTP/1.1",
    };

    private static long HeaderBlockSize(byte[] wire, string version)
    {
        if (!version.StartsWith("HTTP/1", StringComparison.OrdinalIgnoreCase) || wire.Length == 0)
        {
            return -1;
        }

        var end = wire.AsSpan().IndexOf("\r\n\r\n"u8);
        return end < 0 ? -1 : end + 4;
    }

    private static double Round(double value) => value < 0 ? -1 : Math.Round(value, 3);

    private static string? Find(List<HarHeader> headers, string name) =>
        headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonArray Headers(IEnumerable<HarHeader> headers) =>
        new(headers.Select(h => (JsonNode)new JsonObject { ["name"] = h.Name, ["value"] = h.Value }).ToArray());

    private static JsonArray Pairs(IEnumerable<HarHeader> pairs) => Headers(pairs);

    private static JsonArray Cookies(IEnumerable<CookieInfo> cookies) =>
        new(cookies.Select(c =>
        {
            var o = new JsonObject { ["name"] = c.Name, ["value"] = c.Value };
            if (c.Path is not null)
            {
                o["path"] = c.Path;
            }

            if (c.Domain is not null)
            {
                o["domain"] = c.Domain;
            }

            if (c.Expires is not null)
            {
                o["expires"] = c.Expires;
            }

            if (c.HttpOnly)
            {
                o["httpOnly"] = true;
            }

            if (c.Secure)
            {
                o["secure"] = true;
            }

            if (c.SameSite is not null)
            {
                o["sameSite"] = c.SameSite;
            }

            return (JsonNode)o;
        }).ToArray());
}
