using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Harborer.Core.Har;
using Harborer.Core.Model;
using Harborer.Core.Text;

namespace Harborer.Core.Saz;

/// <summary>
/// Imports a SAZ session archive: a zip with <c>raw/NN_c.txt</c> (request bytes),
/// <c>raw/NN_s.txt</c> (response bytes) and <c>raw/NN_m.xml</c> (timers and flags). Each session becomes a HAR 1.2
/// entry in memory, and the result is indexed by the normal parser. Password-protected archives are not supported.
/// </summary>
public static partial class SazImporter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"^raw/([0-9]{1,9})_c\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestFileRegex();

    public static HarLoadResult Import(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var bytes = Convert(zip, cancellationToken);
            var source = new MemoryHarSource(Path.GetFileName(path), bytes, Path.GetFullPath(path));
            var result = HarReader.Load(source, cancellationToken: cancellationToken);
            if (result.Document is null)
            {
                source.Dispose();
            }

            return result;
        }
        catch (InvalidDataException ex)
        {
            return new HarLoadResult { FatalError = $"'{Path.GetFileName(path)}' is not a readable SAZ archive: {ex.Message}" };
        }
        catch (NotSupportedException)
        {
            return new HarLoadResult { FatalError = "Password-protected SAZ archives are not supported. Re-save the sessions without a password." };
        }
        catch (IOException ex)
        {
            return new HarLoadResult { FatalError = $"Cannot read '{path}': {ex.Message}" };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or FormatException)
        {
            return new HarLoadResult { FatalError = $"'{Path.GetFileName(path)}' could not be imported: {ex.Message}" };
        }
    }

    /// <summary>Converts the archive to HAR JSON bytes.</summary>
    public static byte[] Convert(ZipArchive zip, CancellationToken cancellationToken = default)
    {
        var sessions = zip.Entries
            .Select(e => (Entry: e, Match: RequestFileRegex().Match(e.FullName.Replace('\\', '/'))))
            .Where(x => x.Match.Success)
            .Select(x => (Number: int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture), Prefix: x.Entry.FullName[..^"_c.txt".Length], Request: x.Entry))
            .OrderBy(x => x.Number)
            .ToList();

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("log");
            w.WriteString("version", "1.2");
            w.WriteStartObject("creator");
            w.WriteString("name", "SAZ archive (imported by HARborer)");
            w.WriteString("version", HarWriter.CreatorVersion);
            w.WriteEndObject();
            w.WriteStartArray("entries");
            foreach (var s in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = ReadAll(s.Request);
                var response = zip.GetEntry(s.Prefix + "_s.txt") is { } r ? ReadAll(r) : null;
                var meta = zip.GetEntry(s.Prefix + "_m.xml") is { } m ? ReadAll(m) : null;
                WriteEntry(w, s.Number, request, response, meta);
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private const long MaxEntryBytes = 512L * 1024 * 1024;

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > MaxEntryBytes)
            {
                throw new InvalidDataException($"'{entry.FullName}' expands beyond {MaxEntryBytes / (1024 * 1024)} MB.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static void WriteEntry(Utf8JsonWriter w, int number, byte[] requestBytes, byte[]? responseBytes, byte[]? metaBytes)
    {
        var request = RawHttpMessage.Parse(requestBytes);
        var response = responseBytes is { Length: > 0 } ? RawHttpMessage.Parse(responseBytes) : null;
        var meta = SessionMeta.Parse(metaBytes);

        var parts = request.StartLine.Split(' ', 3);
        var method = parts.Length > 0 ? parts[0] : "GET";
        var target = parts.Length > 1 ? parts[1] : "/";
        var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
        var host = request.Header("Host") ?? "";
        string url;
        if (target.Contains("://", StringComparison.Ordinal))
        {
            url = target;
        }
        else if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + target;
        }
        else
        {
            var scheme = meta.IsHttps ? "https" : "http";
            url = $"{scheme}://{host}{target}";
        }

        w.WriteStartObject();
        w.WriteString("startedDateTime", (meta.ClientBeginRequest ?? DateTimeOffset.UnixEpoch).ToString("o", CultureInfo.InvariantCulture));

        // Timings from the archive's session timers.
        var dns = meta.DnsTime ?? -1;
        var ssl = meta.HttpsHandshakeTime is > 0 ? meta.HttpsHandshakeTime.Value : -1;
        var connect = meta.TcpConnectTime is >= 0 ? meta.TcpConnectTime.Value + Math.Max(0, ssl) : -1;
        var send = Span(meta.ServerConnected ?? meta.ProxyBeginRequest, meta.ServerGotRequest);
        var wait = Span(meta.ServerGotRequest, meta.ServerBeginResponse);
        var receive = Span(meta.ServerBeginResponse, meta.ServerDoneResponse);
        var total = Math.Max(0, dns) + Math.Max(0, connect) + Math.Max(0, send) + Math.Max(0, wait) + Math.Max(0, receive);
        w.WriteNumber("time", Math.Round(total, 3));

        w.WriteStartObject("request");
        w.WriteString("method", method);
        w.WriteString("url", url);
        w.WriteString("httpVersion", version);
        WriteHeaders(w, request.Headers);
        w.WriteStartArray("queryString");
        foreach (var q in Http.FormUrlEncoding.ParseQuery(url))
        {
            WritePair(w, q);
        }

        w.WriteEndArray();
        w.WriteStartArray("cookies");
        foreach (var c in Http.Cookies.ParseRequestCookies(request.Headers))
        {
            WritePair(w, new HarHeader(c.Name, c.Value));
        }

        w.WriteEndArray();
        w.WriteNumber("headersSize", request.HeaderBlockLength);
        var requestBody = request.DecodedBody(decodeContentEncoding: false);
        w.WriteNumber("bodySize", requestBody.Length);
        if (requestBody.Length > 0)
        {
            var contentType = request.Header("Content-Type") ?? "";
            w.WriteStartObject("postData");
            w.WriteString("mimeType", contentType);
            WriteText(w, requestBody, contentType);
            w.WriteEndObject();
        }

        w.WriteEndObject();

        w.WriteStartObject("response");
        if (response is null)
        {
            w.WriteNumber("status", 0);
            w.WriteString("statusText", "");
            w.WriteString("httpVersion", "");
            w.WriteStartArray("headers");
            w.WriteEndArray();
            w.WriteStartArray("cookies");
            w.WriteEndArray();
            w.WriteStartObject("content");
            w.WriteNumber("size", 0);
            w.WriteString("mimeType", "");
            w.WriteEndObject();
            w.WriteString("redirectURL", "");
            w.WriteNumber("headersSize", -1);
            w.WriteNumber("bodySize", -1);
            w.WriteString("_error", "No response was recorded for this session.");
        }
        else
        {
            var status = response.StartLine.Split(' ', 3);
            w.WriteNumber("status", status.Length > 1 && int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 0);
            w.WriteString("statusText", status.Length > 2 ? status[2] : "");
            w.WriteString("httpVersion", status.Length > 0 ? status[0] : "HTTP/1.1");
            WriteHeaders(w, response.Headers);
            w.WriteStartArray("cookies");
            foreach (var c in Http.Cookies.ParseSetCookies(response.Headers))
            {
                WritePair(w, new HarHeader(c.Name, c.Value));
            }

            w.WriteEndArray();
            var wireBody = response.DecodedBody(decodeContentEncoding: false);
            var body = response.DecodedBody(decodeContentEncoding: true);
            var mime = response.Header("Content-Type") ?? "";
            w.WriteStartObject("content");
            w.WriteNumber("size", body.Length);
            if (body.Length != wireBody.Length)
            {
                w.WriteNumber("compression", body.Length - wireBody.Length);
            }

            w.WriteString("mimeType", mime);
            if (body.Length > 0)
            {
                WriteText(w, body, mime);
            }

            w.WriteEndObject();
            w.WriteString("redirectURL", response.Header("Location") ?? "");
            w.WriteNumber("headersSize", response.HeaderBlockLength);
            w.WriteNumber("bodySize", wireBody.Length);
        }

        w.WriteEndObject();
        w.WriteStartObject("cache");
        w.WriteEndObject();
        w.WriteStartObject("timings");
        w.WriteNumber("blocked", -1);
        w.WriteNumber("dns", dns);
        w.WriteNumber("connect", connect);
        w.WriteNumber("ssl", ssl);
        w.WriteNumber("send", Math.Max(0, send));
        w.WriteNumber("wait", Math.Max(0, wait));
        w.WriteNumber("receive", Math.Max(0, receive));
        w.WriteEndObject();
        if (meta.HostIp is not null)
        {
            w.WriteString("serverIPAddress", meta.HostIp);
        }

        if (meta.ClientPort is not null)
        {
            w.WriteString("connection", meta.ClientPort);
        }

        if (meta.Comment is not null)
        {
            w.WriteString("comment", meta.Comment);
        }

        w.WriteStartObject("_saz");
        w.WriteNumber("sessionId", number);
        if (meta.ProcessInfo is not null)
        {
            w.WriteString("process", meta.ProcessInfo);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static double Span(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } f && to is { } t && t >= f ? Math.Round((t - f).TotalMilliseconds, 3) : -1;

    private static void WriteHeaders(Utf8JsonWriter w, List<HarHeader> headers)
    {
        w.WriteStartArray("headers");
        foreach (var h in headers)
        {
            WritePair(w, h);
        }

        w.WriteEndArray();
    }

    private static void WritePair(Utf8JsonWriter w, HarHeader pair)
    {
        w.WriteStartObject();
        w.WriteString("name", pair.Name);
        w.WriteString("value", pair.Value);
        w.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter w, byte[] body, string mime)
    {
        if ((MimeTypes.IsTextual(mime) || string.IsNullOrEmpty(mime)) && System.Text.Unicode.Utf8.IsValid(body) && !body.AsSpan().Contains((byte)0))
        {
            w.WriteString("text", Encoding.UTF8.GetString(body));
        }
        else
        {
            w.WriteString("text", System.Convert.ToBase64String(body));
            w.WriteString("encoding", "base64");
        }
    }

    /// <summary>An HTTP/1.x message as the archive stores it: start line, headers, blank line, body as transmitted.</summary>
    internal sealed class RawHttpMessage
    {
        public string StartLine { get; private init; } = "";

        public List<HarHeader> Headers { get; } = [];

        public byte[] Body { get; private init; } = [];

        public int HeaderBlockLength { get; private init; }

        public string? Header(string name) => HarEntry.FindHeader(Headers.ToArray(), name);

        public static RawHttpMessage Parse(byte[] bytes)
        {
            var span = bytes.AsSpan();
            var end = span.IndexOf("\r\n\r\n"u8);
            var separator = 4;
            if (end < 0)
            {
                end = span.IndexOf("\n\n"u8);
                separator = 2;
            }

            var headerText = Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
            var lines = headerText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var message = new RawHttpMessage
            {
                StartLine = lines.Length > 0 ? lines[0].Trim() : "",
                Body = end < 0 ? [] : span[(end + separator)..].ToArray(),
                HeaderBlockLength = end < 0 ? bytes.Length : end + separator,
            };
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    message.Headers.Add(new HarHeader(line[..colon].Trim(), line[(colon + 1)..].Trim()));
                }
            }

            return message;
        }

        /// <summary>The body with chunked transfer coding removed and, optionally, content coding decoded.</summary>
        public byte[] DecodedBody(bool decodeContentEncoding)
        {
            var body = Body;
            if (Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            {
                body = Dechunk(body);
            }

            if (decodeContentEncoding && Header("Content-Encoding") is { } encoding)
            {
                var decoded = ContentDecoder.DecodeIfEncoded(body, encoding, -1);
                body = decoded.Bytes;
            }

            return body;
        }

        private static byte[] Dechunk(byte[] body)
        {
            using var output = new MemoryStream();
            var i = 0;
            while (i < body.Length)
            {
                var lineEnd = body.AsSpan(i).IndexOf("\r\n"u8);
                if (lineEnd < 0)
                {
                    break;
                }

                var sizeText = Encoding.ASCII.GetString(body, i, lineEnd).Split(';')[0].Trim();
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size <= 0)
                {
                    break;
                }

                i += lineEnd + 2;
                var take = Math.Min(size, body.Length - i);
                output.Write(body, i, take);
                i += take + 2;
            }

            return output.ToArray();
        }
    }

    private sealed class SessionMeta
    {
        public DateTimeOffset? ClientBeginRequest { get; private set; }

        public DateTimeOffset? ProxyBeginRequest { get; private set; }

        public DateTimeOffset? ServerConnected { get; private set; }

        public DateTimeOffset? ServerGotRequest { get; private set; }

        public DateTimeOffset? ServerBeginResponse { get; private set; }

        public DateTimeOffset? ServerDoneResponse { get; private set; }

        public double? DnsTime { get; private set; }

        public double? TcpConnectTime { get; private set; }

        public double? HttpsHandshakeTime { get; private set; }

        public string? HostIp { get; private set; }

        public string? ClientPort { get; private set; }

        public string? ProcessInfo { get; private set; }

        public string? Comment { get; private set; }

        public bool IsHttps { get; private set; }

        public static SessionMeta Parse(byte[]? xml)
        {
            var meta = new SessionMeta();
            if (xml is null || xml.Length == 0)
            {
                return meta;
            }

            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                doc.Load(reader);
                if (doc.SelectSingleNode("/Session/SessionTimers") is XmlElement timers)
                {
                    meta.ClientBeginRequest = Time(timers, "ClientBeginRequest");
                    meta.ProxyBeginRequest = Time(timers, "FiddlerBeginRequest"); // attribute name fixed by the SAZ format
                    meta.ServerConnected = Time(timers, "ServerConnected");
                    meta.ServerGotRequest = Time(timers, "ServerGotRequest");
                    meta.ServerBeginResponse = Time(timers, "ServerBeginResponse");
                    meta.ServerDoneResponse = Time(timers, "ServerDoneResponse");
                    meta.DnsTime = Number(timers, "DNSTime");
                    meta.TcpConnectTime = Number(timers, "TCPConnectTime");
                    meta.HttpsHandshakeTime = Number(timers, "HTTPSHandshakeTime");
                }

                foreach (XmlElement flag in doc.SelectNodes("/Session/SessionFlags/SessionFlag")?.OfType<XmlElement>() ?? [])
                {
                    var name = flag.GetAttribute("N");
                    var value = flag.GetAttribute("V");
                    switch (name.ToLowerInvariant())
                    {
                        case "x-hostip":
                            meta.HostIp = value;
                            break;
                        case "x-clientport":
                            meta.ClientPort = value;
                            break;
                        case "x-processinfo":
                            meta.ProcessInfo = value;
                            break;
                        case "ui-comments":
                            meta.Comment = value;
                            break;
                        case "x-https" or "https-client-sessionid" or "x-securepipe":
                            meta.IsHttps = true;
                            break;
                    }
                }
            }
            catch (XmlException)
            {
            }

            return meta;
        }

        private static DateTimeOffset? Time(XmlElement e, string name)
        {
            var text = e.GetAttribute(name);
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value) && value.Year > 1
                ? value
                : null;
        }

        private static double? Number(XmlElement e, string name) =>
            double.TryParse(e.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
