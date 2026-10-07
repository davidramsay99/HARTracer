using HarLens.Core.Model;

namespace HarLens.Core.Engine;

/// <summary>Result of one Send. Each redirect hop that was followed is a separate exchange.</summary>
public sealed class SendOutcome
{
    public List<ExchangeRecord> Exchanges { get; } = [];

    /// <summary>Error that ended the send, if any. The last exchange carries the details when one was started.</summary>
    public string? Error { get; set; }

    public bool Cancelled { get; set; }

    public bool Succeeded => Error is null && !Cancelled;
}

/// <summary>Phase timings in milliseconds. -1 means the phase was not measurable (HAR semantics).</summary>
public sealed class ExchangeTimings
{
    public double Blocked { get; set; } = -1;

    public double Dns { get; set; } = -1;

    /// <summary>TCP connect plus TLS handshake, as in HAR where <c>ssl</c> is included in <c>connect</c>.</summary>
    public double Connect { get; set; } = -1;

    public double Ssl { get; set; } = -1;

    public double Send { get; set; } = -1;

    public double Wait { get; set; } = -1;

    public double Receive { get; set; } = -1;

    /// <summary>Wall-clock total of the exchange.</summary>
    public double Total { get; set; } = -1;
}

public sealed class TlsDetails
{
    public string Protocol { get; set; } = "";

    public string CipherSuite { get; set; } = "";

    /// <summary>ALPN result, for example "h2" or "http/1.1".</summary>
    public string? ApplicationProtocol { get; set; }

    public string Subject { get; set; } = "";

    public string Issuer { get; set; } = "";

    public DateTimeOffset NotBefore { get; set; }

    public DateTimeOffset NotAfter { get; set; }

    public List<string> SubjectAlternativeNames { get; set; } = [];

    public string Thumbprint { get; set; } = "";

    /// <summary>Validation errors reported by the platform, "None" when the chain was valid.</summary>
    public string PolicyErrors { get; set; } = "None";

    /// <summary>The SNI host name presented.</summary>
    public string? ServerName { get; set; }
}

/// <summary>Everything captured about one request/response exchange.</summary>
public sealed class ExchangeRecord
{
    public DateTimeOffset StartedDateTime { get; set; }

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = "";

    /// <summary>HTTP version used on the wire, as "HTTP/1.1" or "HTTP/2".</summary>
    public string HttpVersion { get; set; } = "HTTP/1.1";

    /// <summary>Request headers as sent, including the ones the engine computed (Host, Content-Length).</summary>
    public List<HarHeader> RequestHeaders { get; set; } = [];

    public byte[]? RequestBody { get; set; }

    /// <summary>0 when no response arrived.</summary>
    public int Status { get; set; }

    public string StatusText { get; set; } = "";

    public List<HarHeader> ResponseHeaders { get; set; } = [];

    /// <summary>Response body after decompression when automatic decompression was on.</summary>
    public byte[] ResponseBody { get; set; } = [];

    public bool ResponseBodyTruncated { get; set; }

    /// <summary>Bytes of the body as received on the wire, -1 when unknown.</summary>
    public long ResponseBodyWireSize { get; set; } = -1;

    public bool ResponseBodyDecompressed { get; set; }

    public ExchangeTimings Timings { get; set; } = new();

    public string? RemoteAddress { get; set; }

    public int RemotePort { get; set; }

    public TlsDetails? Tls { get; set; }

    /// <summary>Exact plaintext bytes written to the connection (HTTP/1.1 text or HTTP/2 frames), up to the recording cap.</summary>
    public byte[] WireSent { get; set; } = [];

    /// <summary>Exact plaintext bytes read from the connection, up to the recording cap.</summary>
    public byte[] WireReceived { get; set; } = [];

    public bool WireTruncated { get; set; }

    /// <summary>Notices shown with the result, for example body truncation or an undecodable encoding.</summary>
    public List<string> Notices { get; set; } = [];

    public string? Error { get; set; }

    public string? RedirectLocation { get; set; }
}
