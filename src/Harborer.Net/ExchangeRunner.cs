using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Harborer.Core.Engine;
using Harborer.Core.Http;
using Harborer.Core.Model;

namespace Harborer.Net;

internal sealed record HopResult(ExchangeRecord Record, bool Cancelled);

/// <summary>Sends one hop over its own SocketsHttpHandler, so every hop's DNS, connect and TLS are measured.</summary>
internal static class ExchangeRunner
{
    public static async Task<HopResult> RunAsync(HopRequest hop, SendContext context, CancellationToken cancellationToken)
    {
        bool https = Preflight.IsHttps(hop.Uri);
        var record = new ExchangeRecord
        {
            StartedDateTime = DateTimeOffset.Now,
            Method = hop.Method,
            Url = Preflight.DisplayUrl(hop.Uri),
            HttpVersion = hop.Version == HttpVersionPreference.Http2 && https ? "HTTP/2" : "HTTP/1.1",
        };
        record.Notices.AddRange(hop.Notices);
        var trace = new HopTrace(context) { Start = Stopwatch.GetTimestamp() };

        HttpRequestMessage message;
        try
        {
            message = RequestMessageFactory.Create(hop, context.Options, record.Notices, out var sentBody);
            record.RequestBody = sentBody;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            record.Error = "Invalid request: " + ex.Message;
            record.RequestHeaders = [.. hop.Headers];
            return new HopResult(record, false);
        }

        trace.Request = message;
        var body = new BodyCapture(context.ResponseSizeCap);
        HttpResponseMessage? response = null;
        Version? responseVersion = null;
        List<HarHeader> collectedResponseHeaders = [];
        List<HarHeader> trailers = [];
        List<string> contentEncodings = [];
        bool cancelled = false;
        bool bodyComplete = false;

        var handler = CreateHandler(context, trace);
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            trace.MarkResponseHeaders();
            responseVersion = response.Version;
            record.Status = (int)response.StatusCode;
            record.StatusText = response.ReasonPhrase ?? "";
            record.HttpVersion = VersionText(response.Version);
            if (record.Status is >= 300 and < 400 &&
                HeaderCollector.Values(response.Headers.NonValidated, "Location").FirstOrDefault() is { } location)
            {
                record.RedirectLocation = location;
            }

            await body.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
            bodyComplete = true;
        }
        catch (Exception ex)
        {
            (record.Error, cancelled) = ErrorDescriber.Describe(ex, trace, context, body.Count);
        }
        finally
        {
            trace.BodyEnd = response is null ? 0 : Stopwatch.GetTimestamp();
            if (response is not null)
            {
                bool http2Response = response.Version.Major >= 2;
                collectedResponseHeaders = HeaderCollector.Response(response, http2Response);
                trailers = HeaderCollector.Trailers(response, http2Response);
                contentEncodings = ContentDecoder.ParseEncodings(
                    HeaderCollector.Values(response.Content.Headers.NonValidated, "Content-Encoding"));
            }

            // Disposing before reading the trace guarantees nothing more is written to the recorders.
            response?.Dispose();
            client.Dispose();
        }

        long end = Stopwatch.GetTimestamp();
        var wire = trace.Wire?.Snapshot();
        bool http2 = (responseVersion ?? trace.NegotiatedVersion)?.Major >= 2;
        if (responseVersion is null && trace.NegotiatedVersion is { } negotiated)
        {
            record.HttpVersion = VersionText(negotiated);
        }

        record.RemoteAddress = trace.RemoteAddress;
        record.RemotePort = trace.RemotePort;
        record.Tls = https ? trace.Tls : null;
        record.WireSent = wire?.Sent ?? [];
        record.WireReceived = wire?.Received ?? [];
        record.WireTruncated = wire?.Truncated ?? false;
        if (record.WireTruncated)
        {
            record.Notices.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Wire recording truncated at {context.Settings.MaxRecordedWireBytes} bytes per direction"));
        }

        record.RequestHeaders = !http2 && WireMessageParser.ParseRequestHead(record.WireSent) is { } requestHead
            ? requestHead.Headers
            : HeaderCollector.Request(message, http2);
        message.Dispose();

        if (response is not null)
        {
            if (!http2 && WireMessageParser.ParseFinalResponseHead(record.WireReceived) is { } responseHead)
            {
                record.ResponseHeaders = [.. responseHead.Headers, .. trailers];
                record.StatusText = responseHead.ReasonPhrase;
            }
            else
            {
                record.ResponseHeaders = collectedResponseHeaders;
            }

            FillBody(record, body, contentEncodings, context, !bodyComplete);
        }

        record.Timings = ComputeTimings(trace, wire, http2, https, end);
        record.Notices.AddRange(trace.Notices);
        return new HopResult(record, cancelled);
    }

    internal static string VersionText(Version version) =>
        version.Major >= 2
            ? string.Create(CultureInfo.InvariantCulture, $"HTTP/{version.Major}")
            : string.Create(CultureInfo.InvariantCulture, $"HTTP/{version.Major}.{version.Minor}");

    private static SocketsHttpHandler CreateHandler(SendContext context, HopTrace trace)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,

            // Only the explicit proxy, never the system or default proxy, never WPAD.
            UseProxy = context.Proxy is not null,
            Proxy = context.Proxy,
            Credentials = null,
            PreAuthenticate = false,

            // No traceparent or Request-Id headers: send exactly what was composed.
            ActivityHeadersPropagator = null,

            // Truncated responses close the connection instead of draining the rest of the body.
            MaxResponseDrainSize = 0,
            RequestHeaderEncodingSelector = static (_, _) => Encoding.UTF8,
            ConnectCallback = trace.ConnectAsync,
            PlaintextStreamFilter = trace.FilterAsync,
            SslOptions = new SslClientAuthenticationOptions
            {
                // No CRL, OCSP or AIA fetches: the engine connects only where the user pointed it.
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true,
                },
                RemoteCertificateValidationCallback = trace.ValidateServerCertificate,

                // Every hop is a full handshake, so the ssl timing is comparable between sends.
                AllowTlsResume = false,
                ClientCertificateContext = context.ClientCertificate,
            },
        };

        if (context.Options.ConnectTimeout is { } connectTimeout && connectTimeout > TimeSpan.Zero)
        {
            handler.ConnectTimeout = connectTimeout;
        }

        return handler;
    }

    private static void FillBody(ExchangeRecord record, BodyCapture body, List<string> encodings, SendContext context, bool incomplete)
    {
        byte[] raw = body.ToArray();
        record.ResponseBodyWireSize = raw.Length;
        record.ResponseBodyTruncated = body.Truncated;
        if (body.Truncated)
        {
            record.Notices.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Response body truncated at {context.ResponseSizeCap} bytes (response size cap)"));
        }

        if (!context.Options.AutoDecompress || encodings.Count == 0)
        {
            record.ResponseBody = raw;
            return;
        }

        var decoded = ContentDecoder.Decode(raw, encodings, context.ResponseSizeCap, body.Truncated || incomplete);
        record.ResponseBody = decoded.Body;
        record.ResponseBodyDecompressed = decoded.Decoded;
        record.ResponseBodyTruncated |= decoded.Truncated;
        record.Notices.AddRange(decoded.Notices);
    }

    private static ExchangeTimings ComputeTimings(HopTrace trace, WireSnapshot? wire, bool http2, bool https, long end)
    {
        var timings = new ExchangeTimings
        {
            Blocked = Ms(trace.Start, trace.ConnectStart),
            Dns = trace.DnsLookedUp ? Ms(trace.DnsStart, trace.DnsEnd) : -1,
            Total = Ms(trace.Start, end),
        };

        double tcp = Ms(trace.TcpStart, trace.TcpEnd);
        if (https && trace.TlsEnd != 0)
        {
            // TLS starts when the connect callback returns, or after the CONNECT response through a proxy.
            var tunnel = trace.Tunnel?.Snapshot();
            long tlsStart = tunnel is { FirstReadEnd: > 0 } ? tunnel.FirstReadEnd : trace.TcpEnd;
            timings.Ssl = Ms(tlsStart, trace.TlsEnd);
        }

        // HAR counts ssl inside connect.
        timings.Connect = tcp < 0 ? -1 : timings.Ssl >= 0 ? Ms(trace.TcpStart, trace.TlsEnd) : tcp;

        if (wire is { FirstWriteStart: > 0 })
        {
            long lastWrite;
            long firstByte;
            long lastByte = trace.BodyEnd;
            if (http2)
            {
                // HTTP/2 frames interleave (settings, window updates), so the response headers mark the first byte
                // and the end of the body read marks the last.
                lastWrite = trace.LastWriteAtHeaders;
                firstByte = trace.HeadersReceived;
            }
            else
            {
                // One exchange per connection: the last read that returned data delivered the last body byte.
                lastWrite = wire.LastWriteEndBeforeFirstRead;
                firstByte = wire.FirstReadEnd;
                if (trace.BodyEnd != 0 && wire.LastReadEnd >= firstByte && wire.LastReadEnd <= trace.BodyEnd)
                {
                    lastByte = wire.LastReadEnd;
                }
            }

            if (lastWrite == 0)
            {
                lastWrite = wire.LastWriteEnd;
            }

            // A write completion observed after the first byte (continuation delay) ends the send phase at that byte.
            if (firstByte != 0 && lastWrite > firstByte)
            {
                lastWrite = firstByte;
            }

            timings.Send = Ms(wire.FirstWriteStart, lastWrite);
            timings.Wait = Ms(lastWrite, firstByte);
            timings.Receive = Ms(firstByte, lastByte);
        }

        return timings;
    }

    private static double Ms(long from, long to) =>
        from == 0 || to == 0 || to < from ? -1 : Math.Round(Stopwatch.GetElapsedTime(from, to).TotalMilliseconds, 3);

    /// <summary>Reads the raw response body up to the size cap.</summary>
    private sealed class BodyCapture
    {
        private readonly long _cap;
        private readonly MemoryStream _buffer = new();

        public BodyCapture(long cap)
        {
            _cap = cap;
        }

        public bool Truncated { get; private set; }

        public long Count => _buffer.Length;

        public async Task ReadAsync(HttpContent content, CancellationToken cancellationToken)
        {
            var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                byte[] chunk = new byte[81920];
                while (true)
                {
                    long room = _cap - _buffer.Length;
                    if (room == 0)
                    {
                        // At the cap: one more byte means the body was longer.
                        Truncated = await stream.ReadAsync(chunk.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0;
                        return;
                    }

                    int read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, room)), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    _buffer.Write(chunk, 0, read);
                }
            }
        }

        public byte[] ToArray() => _buffer.ToArray();
    }
}
