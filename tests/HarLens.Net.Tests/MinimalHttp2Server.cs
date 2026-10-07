using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace HarLens.Net.Tests;

/// <summary>
/// Just enough HTTP/2 over TLS (ALPN h2) to answer each request stream with a fixed response: ":status 200",
/// "content-type: text/plain", "x-h2: yes" and a short body. The request header block is not decoded.
/// </summary>
internal sealed class MinimalHttp2Server : IAsyncDisposable
{
    public const string ResponseText = "hello h2";

    private const byte Data = 0x0;
    private const byte Headers = 0x1;
    private const byte Settings = 0x4;
    private const byte Ping = 0x6;
    private const byte GoAway = 0x7;
    private const byte EndStream = 0x1;
    private const byte Ack = 0x1;
    private const byte EndHeaders = 0x4;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _streams;

    public MinimalHttp2Server()
    {
        _listener.Start();
        _loop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int StreamsAnswered => Volatile.Read(ref _streams);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        _stop.Dispose();
    }

    private static byte[] ResponseHeaderBlock()
    {
        var block = new List<byte> { 0x88 }; // :status 200 (static table index 8)
        block.AddRange([0x0F, 0x10]); // content-type (static index 31), literal without indexing
        block.Add(10);
        block.AddRange("text/plain"u8.ToArray());
        block.Add(0x00); // literal without indexing, new name
        block.Add(4);
        block.AddRange("x-h2"u8.ToArray());
        block.Add(3);
        block.AddRange("yes"u8.ToArray());
        return [.. block];
    }

    private static byte[] Frame(byte type, byte flags, int stream, byte[] payload)
    {
        byte[] frame = new byte[9 + payload.Length];
        frame[0] = (byte)(payload.Length >> 16);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        frame[3] = type;
        frame[4] = flags;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), stream);
        payload.CopyTo(frame, 9);
        return frame;
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), token);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return buffer;
    }

    private async Task AcceptAsync()
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

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var token = _stop.Token;
        try
        {
            using (client)
            await using (var ssl = new SslStream(client.GetStream()))
            {
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = TestCertificates.Server,
                        ApplicationProtocols = [SslApplicationProtocol.Http2],
                    },
                    token);
                if (await ReadExactAsync(ssl, 24, token) is null)
                {
                    return;
                }

                await ssl.WriteAsync(Frame(Settings, 0, 0, []), token);
                while (true)
                {
                    byte[]? head = await ReadExactAsync(ssl, 9, token);
                    if (head is null)
                    {
                        return;
                    }

                    int length = (head[0] << 16) | (head[1] << 8) | head[2];
                    byte type = head[3];
                    byte flags = head[4];
                    int stream = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(5)) & 0x7FFFFFFF;
                    byte[] payload = length == 0 ? [] : await ReadExactAsync(ssl, length, token) ?? [];

                    switch (type)
                    {
                        case Settings when (flags & Ack) == 0:
                            await ssl.WriteAsync(Frame(Settings, Ack, 0, []), token);
                            break;
                        case Ping when (flags & Ack) == 0:
                            await ssl.WriteAsync(Frame(Ping, Ack, 0, payload), token);
                            break;
                        case Headers when (flags & EndStream) != 0:
                        case Data when (flags & EndStream) != 0:
                            await ssl.WriteAsync(Frame(Headers, EndHeaders, stream, ResponseHeaderBlock()), token);
                            await ssl.WriteAsync(Frame(Data, EndStream, stream, Encoding.ASCII.GetBytes(ResponseText)), token);
                            Interlocked.Increment(ref _streams);
                            break;
                        case GoAway:
                            return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
        {
        }
    }
}
