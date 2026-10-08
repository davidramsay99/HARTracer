using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Harborer.Core.Tests.Fixtures;

/// <summary>
/// Writes large synthetic HAR files at test time (the 200,000-entry and 1 GB fixtures are generated, not committed).
/// Output is deterministic for a given argument set and cached in the temp directory between runs.
/// </summary>
internal static class LargeHarGenerator
{
    private static readonly string[] Hosts = ["www.contoso.test", "api.contoso.test", "cdn.fabrikam.test", "static.northwind.test", "login.contoso.test"];
    private static readonly string[] Mimes = ["application/json", "text/html", "image/png", "application/javascript", "text/css", "font/woff2"];
    private static readonly int[] Statuses = [200, 200, 200, 200, 200, 204, 301, 304, 404, 500, 503, 0];

    /// <summary>Returns the path of a HAR with <paramref name="entries"/> entries whose bodies average <paramref name="bodyBytes"/> bytes.</summary>
    public static string Ensure(int entries, int bodyBytes, string label)
    {
        var path = Path.Combine(FixturePaths.Generated, $"{label}-{entries}-{bodyBytes}.har");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        var temp = path + ".partial";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Write(stream, entries, bodyBytes);
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }

    public static void Write(Stream stream, int entries, int bodyBytes)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = true });
        w.WriteStartObject();
        w.WriteStartObject("log");
        w.WriteString("version", "1.2");
        w.WriteStartObject("creator");
        w.WriteString("name", "Harborer.LargeHarGenerator");
        w.WriteString("version", "1.0");
        w.WriteEndObject();
        w.WriteStartArray("pages");
        w.WriteStartObject();
        w.WriteString("startedDateTime", "2026-10-07T09:00:00.000Z");
        w.WriteString("id", "page_1");
        w.WriteString("title", "generated");
        w.WriteStartObject("pageTimings");
        w.WriteNumber("onContentLoad", 1000);
        w.WriteNumber("onLoad", 2000);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteStartArray("entries");

        var start = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        var bodyChunk = new StringBuilder();
        while (bodyChunk.Length < Math.Max(bodyBytes * 2, 64))
        {
            bodyChunk.Append("{\"id\":12345,\"name\":\"generated item\",\"tags\":[\"alpha\",\"beta\"],\"ok\":true},");
        }

        var body = bodyChunk.ToString();
        var rng = new Random(42);
        for (var i = 0; i < entries; i++)
        {
            var host = Hosts[i % Hosts.Length];
            var mime = Mimes[i % Mimes.Length];
            var status = Statuses[i % Statuses.Length];
            // Mostly increasing start times with some jitter so sorting does real work.
            var started = start.AddMilliseconds(i * 5 + rng.Next(0, 40));
            var len = bodyBytes <= 0 ? 0 : Math.Max(1, bodyBytes / 2 + rng.Next(0, bodyBytes));

            w.WriteStartObject();
            w.WriteString("pageref", "page_1");
            w.WriteString("startedDateTime", started.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            var wait = 20 + (i % 97) * 7.5;
            w.WriteNumber("time", wait + 3);
            w.WriteStartObject("request");
            w.WriteString("method", i % 7 == 0 ? "POST" : "GET");
            w.WriteString("url", $"https://{host}/api/v{i % 3}/items/{i}?page={i % 50}&q=search+term");
            w.WriteString("httpVersion", "http/2.0");
            w.WriteStartArray("headers");
            Header(w, ":authority", host);
            Header(w, ":method", "GET");
            Header(w, ":path", $"/api/items/{i}");
            Header(w, ":scheme", "https");
            Header(w, "accept", "application/json, text/plain, */*");
            Header(w, "accept-encoding", "gzip, deflate, br, zstd");
            Header(w, "accept-language", "en-GB,en;q=0.9");
            Header(w, "user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Generated/1.0");
            Header(w, "x-correlation-id", $"corr-{i:x8}");
            w.WriteEndArray();
            w.WriteStartArray("queryString");
            w.WriteEndArray();
            w.WriteStartArray("cookies");
            w.WriteEndArray();
            w.WriteNumber("headersSize", -1);
            w.WriteNumber("bodySize", 0);
            w.WriteEndObject();

            w.WriteStartObject("response");
            w.WriteNumber("status", status);
            w.WriteString("statusText", status == 200 ? "OK" : "");
            w.WriteString("httpVersion", "http/2.0");
            w.WriteStartArray("headers");
            Header(w, "content-type", mime);
            Header(w, "date", "Wed, 07 Oct 2026 09:00:00 GMT");
            Header(w, "x-cache", i % 3 == 0 ? "TCP_MISS" : "TCP_HIT");
            Header(w, "x-azure-ref", $"20261007T090000Z-{i:x10}");
            Header(w, "x-ms-request-id", $"{i:x8}-0000-4000-8000-000000000000");
            w.WriteEndArray();
            w.WriteStartArray("cookies");
            w.WriteEndArray();
            w.WriteStartObject("content");
            w.WriteNumber("size", len);
            w.WriteString("mimeType", mime);
            if (len > 0)
            {
                w.WriteString("text", body.AsSpan(0, len));
            }

            w.WriteEndObject();
            w.WriteString("redirectURL", "");
            w.WriteNumber("headersSize", -1);
            w.WriteNumber("bodySize", len / 3);
            w.WriteNumber("_transferSize", len / 3 + 200);
            w.WriteEndObject();
            w.WriteStartObject("cache");
            w.WriteEndObject();
            w.WriteStartObject("timings");
            w.WriteNumber("blocked", 0.5);
            w.WriteNumber("dns", -1);
            w.WriteNumber("ssl", -1);
            w.WriteNumber("connect", -1);
            w.WriteNumber("send", 0.2);
            w.WriteNumber("wait", wait);
            w.WriteNumber("receive", 2.3);
            w.WriteEndObject();
            w.WriteString("_resourceType", i % 4 == 0 ? "fetch" : "xhr");
            w.WriteString("_priority", "High");
            w.WriteEndObject();
            if ((i & 1023) == 0)
            {
                w.Flush();
            }
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.Flush();
    }

    private static void Header(Utf8JsonWriter w, string name, string value)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("value", value);
        w.WriteEndObject();
    }
}
