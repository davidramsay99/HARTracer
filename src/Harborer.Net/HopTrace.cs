using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Harborer.Core.Engine;
using Harborer.Core.Http;

namespace Harborer.Net;

/// <summary>
/// Everything measured about the connection of one hop. Its methods are the SocketsHttpHandler callbacks:
/// <see cref="ConnectAsync"/> (overrides, DNS, TCP), <see cref="FilterAsync"/> (end of TLS, wire recording) and
/// <see cref="ValidateServerCertificate"/>. Timestamps are Stopwatch ticks; 0 means "did not happen".
/// </summary>
internal sealed class HopTrace
{
    private readonly SendContext _context;

    public HopTrace(SendContext context)
    {
        _context = context;
    }

    /// <summary>Our request, to tell its connection apart from a proxy CONNECT connection in the filter.</summary>
    public HttpRequestMessage? Request { get; set; }

    public long Start { get; set; }

    public long ConnectStart { get; private set; }

    public bool DnsLookedUp { get; private set; }

    public long DnsStart { get; private set; }

    public long DnsEnd { get; private set; }

    public long TcpStart { get; private set; }

    public long TcpEnd { get; private set; }

    public long TlsEnd { get; private set; }

    public long HeadersReceived { get; private set; }

    public long LastWriteAtHeaders { get; private set; }

    public long BodyEnd { get; set; }

    public string? RemoteAddress { get; private set; }

    public int RemotePort { get; private set; }

    /// <summary>curl-style message for a DNS or TCP failure inside the connect callback.</summary>
    public string? ConnectError { get; private set; }

    /// <summary>curl-style message for a rejected server certificate.</summary>
    public string? CertificateError { get; private set; }

    public TlsDetails? Tls { get; private set; }

    public Version? NegotiatedVersion { get; private set; }

    public WireRecorder? Wire { get; private set; }

    /// <summary>Timestamps of the proxy CONNECT connection, when an https request was tunnelled.</summary>
    public WireRecorder? Tunnel { get; private set; }

    public List<string> Notices { get; } = [];

    public void MarkResponseHeaders()
    {
        HeadersReceived = Stopwatch.GetTimestamp();
        LastWriteAtHeaders = Wire?.LastWriteEnd ?? 0;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext connection, CancellationToken cancellationToken)
    {
        ConnectStart = Stopwatch.GetTimestamp();
        string endpointHost = connection.DnsEndPoint.Host;
        string host = Unbracket(endpointHost);
        int port = connection.DnsEndPoint.Port;
        string targetHost = host;
        int targetPort = port;
        bool addressList = false;

        // Overrides apply to the origin only. With a proxy the endpoint here is the proxy itself.
        if (_context.Proxy is null)
        {
            var match = _context.Options.ConnectOverrides.FirstOrDefault(o => o.Matches(host, port) || o.Matches(endpointHost, port));
            if (match is not null)
            {
                if (!string.IsNullOrWhiteSpace(match.TargetHost))
                {
                    targetHost = Unbracket(match.TargetHost.Trim());
                }

                if (match.TargetPort > 0)
                {
                    targetPort = match.TargetPort;
                }

                addressList = match.Kind == ConnectOverrideKind.Resolve;
                Notices.Add($"Connect override: {host}:{port} was connected through {targetHost}:{targetPort}");
            }
        }

        IPAddress[] addresses = ParseAddresses(targetHost, addressList);
        if (addresses.Length == 0)
        {
            DnsLookedUp = true;
            DnsStart = Stopwatch.GetTimestamp();
            try
            {
                addresses = await Dns.GetHostAddressesAsync(targetHost, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                DnsEnd = Stopwatch.GetTimestamp();
                ConnectError = $"Could not resolve host: {targetHost}";
                throw;
            }

            DnsEnd = Stopwatch.GetTimestamp();
            if (addresses.Length == 0)
            {
                ConnectError = $"Could not resolve host: {targetHost}";
                throw new SocketException((int)SocketError.HostNotFound);
            }
        }

        TcpStart = Stopwatch.GetTimestamp();
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, targetPort), cancellationToken).ConfigureAwait(false);
                TcpEnd = Stopwatch.GetTimestamp();
                RemoteAddress = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
                RemotePort = targetPort;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        long elapsed = (long)Stopwatch.GetElapsedTime(TcpStart).TotalMilliseconds;
        ConnectError = string.Create(
            CultureInfo.InvariantCulture,
            $"Failed to connect to {targetHost} port {targetPort} after {elapsed} ms: {Describe(lastError)}");
        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    public ValueTask<Stream> FilterAsync(SocketsHttpPlaintextStreamFilterContext filter, CancellationToken cancellationToken)
    {
        long now = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(filter.InitialRequestMessage, Request))
        {
            // The proxy CONNECT connection: it carries the CONNECT exchange and then the tunnelled TLS bytes, so only
            // its timestamps are kept. Its first read (the CONNECT response) is where the TLS handshake begins.
            var tunnel = new WireRecorder(0, capture: false);
            Tunnel = tunnel;
            return ValueTask.FromResult<Stream>(new RecordingStream(filter.PlaintextStream, tunnel));
        }

        NegotiatedVersion = filter.NegotiatedHttpVersion;
        if (filter.PlaintextStream is SslStream ssl)
        {
            TlsEnd = now;
            var tls = Tls ?? new TlsDetails();
            CertificateInspector.FillSession(tls, ssl);
            Tls = tls;
        }

        var wire = new WireRecorder(_context.Settings.MaxRecordedWireBytes);
        Wire = wire;
        return ValueTask.FromResult<Stream>(new RecordingStream(filter.PlaintextStream, wire));
    }

    public bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var tls = new TlsDetails();
        string? targetHost = (sender as SslStream)?.TargetHostName;
        tls.ServerName = CertificateInspector.SniName(targetHost);
        if (certificate is not null)
        {
            CertificateInspector.FillCertificate(tls, certificate);
        }

        tls.PolicyErrors = CertificateInspector.DescribePolicyErrors(errors, chain);
        Tls = tls;

        if (errors == SslPolicyErrors.None || _context.Options.Insecure)
        {
            return true;
        }

        CertificateError = CertificateInspector.FailureMessage(
            errors, chain, certificate, targetHost ?? Request?.RequestUri?.Host ?? "");
        return false;
    }

    private static string Unbracket(string host) =>
        host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>IP literals need no lookup. A --resolve target may list several addresses separated by commas.</summary>
    private static IPAddress[] ParseAddresses(string host, bool allowList)
    {
        if (IPAddress.TryParse(host, out var single))
        {
            return [single];
        }

        if (!allowList || !host.Contains(','))
        {
            return [];
        }

        var list = new List<IPAddress>();
        foreach (string part in host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(Unbracket(part), out var address))
            {
                return [];
            }

            list.Add(address);
        }

        return [.. list];
    }

    private static string Describe(SocketException? error) => error?.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "Connection refused",
        SocketError.TimedOut => "Connection timed out",
        SocketError.HostUnreachable => "No route to host",
        SocketError.NetworkUnreachable => "Network is unreachable",
        SocketError.AccessDenied => "Permission denied",
        null => "Could not connect",
        _ => error.Message,
    };
}
