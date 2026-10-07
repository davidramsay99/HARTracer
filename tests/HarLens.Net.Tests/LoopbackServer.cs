using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HarLens.Net.Tests;

/// <summary>One request as the loopback server received it.</summary>
internal sealed class ReceivedRequest
{
    public required string RequestLine { get; init; }

    public required string Method { get; init; }

    /// <summary>Request target as sent: origin form (/p), absolute form (through a proxy) or authority form (CONNECT).</summary>
    public required string Target { get; init; }

    public required List<KeyValuePair<string, string>> Headers { get; init; }

    public required byte[] RawHead { get; init; }

    public required byte[] Body { get; init; }

    public string? Sni { get; init; }

    public bool Tls { get; init; }

    public string? ClientCertificateThumbprint { get; init; }

    public int ConnectionId { get; init; }

    /// <summary>Path and query, also for an absolute-form target.</summary>
    public string Path => Target.StartsWith('/') ? Target : Uri.TryCreate(Target, UriKind.Absolute, out var uri) ? uri.PathAndQuery : Target;

    public string BodyText => Encoding.UTF8.GetString(Body);

    public byte[] RawBytes => [.. RawHead, .. Body];

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } h ? h.Value : null;

    public bool HasHeader(string name) => Headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A response the loopback server writes. <see cref="Head"/> gives exact control of casing, order and duplicates.</summary>
internal sealed class ScriptedResponse
{
    public int Status { get; init; } = 200;

    public string Reason { get; init; } = "OK";

    public List<KeyValuePair<string, string>> Headers { get; init; } = [];

    public byte[] Body { get; init; } = [];

    /// <summary>Adds Content-Length unless <see cref="Headers"/> already has one.</summary>
    public bool AddContentLength { get; init; } = true;

    /// <summary>Exact head bytes (status line through the blank line). Overrides Status, Reason and Headers.</summary>
    public string? Head { get; init; }

    /// <summary>Wait before writing anything.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Write the head and this many body bytes, then wait <see cref="StallFor"/> before the rest.</summary>
    public int? StallAfterBodyBytes { get; init; }

    public TimeSpan StallFor { get; init; }

    /// <summary>Close the connection without writing anything.</summary>
    public bool CloseWithoutResponse { get; init; }

    public static ScriptedResponse Text(string text, int status = 200, string reason = "OK") => new()
    {
        Status = status,
        Reason = reason,
        Headers = [new("Content-Type", "text/plain; charset=utf-8")],
        Body = Encoding.UTF8.GetBytes(text),
    };

    public static ScriptedResponse Redirect(int status, string location) => new()
    {
        Status = status,
        Reason = status switch { 301 => "Moved Permanently", 302 => "Found", 303 => "See Other", 307 => "Temporary Redirect", 308 => "Permanent Redirect", _ => "Redirect" },
        Headers = [new("Location", location)],
    };

    public byte[] SerializeHead()
    {
        if (Head is not null)
        {
            return Encoding.UTF8.GetBytes(Head);
        }

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {Status} {Reason}\r\n");
        foreach (var header in Headers)
        {
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        if (AddContentLength && !Headers.Any(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Append(CultureInfo.InvariantCulture, $"Content-Length: {Body.Length}\r\n");
        }

        builder.Append("\r\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}

internal sealed class LoopbackServerOptions
{
    /// <summary>Serve TLS with this certificate.</summary>
    public X509Certificate2? Certificate { get; init; }

    public bool RequireClientCertificate { get; init; }

    /// <summary>Answer CONNECT with 200 and keep serving requests inside the tunnel (TLS when a certificate is set).</summary>
    public bool TunnelProxy { get; init; }
}

/// <summary>
/// In-process HTTP/1.1 server on 127.0.0.1 and an ephemeral port. Records every request and answers each
/// with a scripted response. Optional TLS captures the SNI and the client certificate.
/// </summary>
internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<ReceivedRequest, ScriptedResponse> _respond;
    private readonly LoopbackServerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _connections;

    private LoopbackServer(Func<ReceivedRequest, ScriptedResponse> respond, LoopbackServerOptions options)
    {
        _respond = respond;
        _options = options;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool UsesTls => _options.Certificate is not null && !_options.TunnelProxy;

    public string BaseUrl => $"{(UsesTls ? "https" : "http")}://127.0.0.1:{Port}";

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    public ConcurrentQueue<Exception> Errors { get; } = new();

    public int ConnectionCount => Volatile.Read(ref _connections);

    public static LoopbackServer Start(Func<ReceivedRequest, ScriptedResponse> respond, LoopbackServerOptions? options = null) =>
        new(respond, options ?? new LoopbackServerOptions());

    public static LoopbackServer Start(ScriptedResponse response, LoopbackServerOptions? options = null) =>
        Start(_ => response, options);

    public string Url(string pathAndQuery) => BaseUrl + pathAndQuery;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            int id = Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeConnectionAsync(client, id));
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, int connectionId)
    {
        var token = _stop.Token;
        try
        {
            using (client)
            {
                Stream stream = client.GetStream();
                string? sni = null;
                string? clientThumbprint = null;
                bool tls = false;
                if (_options.Certificate is not null && !_options.TunnelProxy)
                {
                    (stream, sni, clientThumbprint) = await StartTlsAsync(stream, token);
                    tls = true;
                }

                var reader = new HeadReader(stream);
                while (!token.IsCancellationRequested)
                {
                    var request = await reader.ReadRequestAsync(sni, clientThumbprint, tls, connectionId, token);
                    if (request is null)
                    {
                        return;
                    }

                    Requests.Enqueue(request);
                    if (_options.TunnelProxy && request.Method == "CONNECT")
                    {
                        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                        if (_options.Certificate is not null)
                        {
                            (stream, sni, clientThumbprint) = await StartTlsAsync(stream, token);
                            tls = true;
                            reader = new HeadReader(stream);
                        }

                        continue;
                    }

                    var response = _respond(request);
                    if (!await WriteResponseAsync(stream, response, token))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            Errors.Enqueue(ex);
        }
    }

    private async Task<(Stream Stream, string? Sni, string? ClientThumbprint)> StartTlsAsync(Stream inner, CancellationToken token)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = _options.Certificate,
                ClientCertificateRequired = _options.RequireClientCertificate,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
            token);
        string? sni = string.IsNullOrEmpty(ssl.TargetHostName) ? null : ssl.TargetHostName;
        string? thumbprint = (ssl.RemoteCertificate as X509Certificate2)?.Thumbprint;
        return (ssl, sni, thumbprint);
    }

    private static async Task<bool> WriteResponseAsync(Stream stream, ScriptedResponse response, CancellationToken token)
    {
        if (response.Delay > TimeSpan.Zero)
        {
            await Task.Delay(response.Delay, token);
        }

        if (response.CloseWithoutResponse)
        {
            return false;
        }

        await stream.WriteAsync(response.SerializeHead(), token);
        if (response.StallAfterBodyBytes is int first)
        {
            await stream.WriteAsync(response.Body.AsMemory(0, first), token);
            await stream.FlushAsync(token);
            await Task.Delay(response.StallFor, token);
            await stream.WriteAsync(response.Body.AsMemory(first), token);
        }
        else
        {
            await stream.WriteAsync(response.Body, token);
        }

        await stream.FlushAsync(token);
        return true;
    }

    /// <summary>Buffered reader for request heads and Content-Length or chunked bodies.</summary>
    private sealed class HeadReader
    {
        private readonly Stream _stream;
        private byte[] _buffer = new byte[16384];
        private int _start;
        private int _end;

        public HeadReader(Stream stream)
        {
            _stream = stream;
        }

        public async Task<ReceivedRequest?> ReadRequestAsync(string? sni, string? clientThumbprint, bool tls, int connectionId, CancellationToken token)
        {
            int headEnd;
            while ((headEnd = IndexOf("\r\n\r\n"u8)) < 0)
            {
                if (!await FillAsync(token))
                {
                    return null;
                }
            }

            byte[] rawHead = _buffer.AsSpan(_start, headEnd + 4 - _start).ToArray();
            _start = headEnd + 4;
            string[] lines = Encoding.UTF8.GetString(rawHead).Split("\r\n");
            string requestLine = lines[0];
            string[] parts = requestLine.Split(' ');
            var headers = new List<KeyValuePair<string, string>>();
            foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                headers.Add(new(line[..colon], line[(colon + 1)..].Trim()));
            }

            string? lengthText = headers.FirstOrDefault(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value;
            bool chunked = headers.Any(h => h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                h.Value.Contains("chunked", StringComparison.OrdinalIgnoreCase));
            byte[] body = chunked
                ? await ReadChunkedAsync(token)
                : await ReadExactAsync(lengthText is null ? 0 : int.Parse(lengthText, CultureInfo.InvariantCulture), token);

            return new ReceivedRequest
            {
                RequestLine = requestLine,
                Method = parts[0],
                Target = parts.Length > 1 ? parts[1] : "",
                Headers = headers,
                RawHead = rawHead,
                Body = body,
                Sni = sni,
                Tls = tls,
                ClientCertificateThumbprint = clientThumbprint,
                ConnectionId = connectionId,
            };
        }

        private async Task<byte[]> ReadChunkedAsync(CancellationToken token)
        {
            using var body = new MemoryStream();
            while (true)
            {
                string sizeLine = await ReadLineAsync(token);
                int size = int.Parse(sizeLine.Split(';')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    while ((await ReadLineAsync(token)).Length > 0)
                    {
                    }

                    return body.ToArray();
                }

                body.Write(await ReadExactAsync(size, token));
                await ReadLineAsync(token);
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken token)
        {
            int end;
            while ((end = IndexOf("\r\n"u8)) < 0)
            {
                if (!await FillAsync(token))
                {
                    throw new IOException("Connection closed inside a chunked body");
                }
            }

            string line = Encoding.ASCII.GetString(_buffer, _start, end - _start);
            _start = end + 2;
            return line;
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken token)
        {
            while (_end - _start < count)
            {
                if (!await FillAsync(token))
                {
                    throw new IOException("Connection closed inside a request body");
                }
            }

            byte[] data = _buffer.AsSpan(_start, count).ToArray();
            _start += count;
            return data;
        }

        private int IndexOf(ReadOnlySpan<byte> marker)
        {
            int index = _buffer.AsSpan(_start, _end - _start).IndexOf(marker);
            return index < 0 ? -1 : _start + index;
        }

        private async Task<bool> FillAsync(CancellationToken token)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), token);
            _end += read;
            return read > 0;
        }
    }
}
