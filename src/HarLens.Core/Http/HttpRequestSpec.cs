namespace HarLens.Core.Http;

/// <summary>A single request header row in the composer. Disabled rows are kept but never sent or exported.</summary>
public sealed class HeaderEntry
{
    public HeaderEntry()
    {
    }

    public HeaderEntry(string name, string value, bool enabled = true)
    {
        Name = name;
        Value = value;
        Enabled = enabled;
    }

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public HeaderEntry Clone() => new(Name, Value, Enabled);

    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>How the composer edits the body. Text-based modes keep their content in <see cref="RequestBody.Text"/>.</summary>
public enum BodyMode
{
    None,
    Raw,
    Json,
    FormUrlEncoded,
    Multipart,
    BinaryFile,
}

/// <summary>One part of a multipart/form-data body.</summary>
public sealed class MultipartPart
{
    public string Name { get; set; } = "";

    /// <summary>Literal text value. Null when the part comes from a file.</summary>
    public string? Value { get; set; }

    /// <summary>File whose bytes form the part (curl <c>-F name=@file</c>).</summary>
    public string? FilePath { get; set; }

    /// <summary>When true the file content is sent as a plain text field without a filename (curl <c>-F name=&lt;file</c>).</summary>
    public bool FileContentAsValue { get; set; }

    /// <summary>Filename reported in Content-Disposition. Defaults to the file's name for file parts.</summary>
    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public bool Enabled { get; set; } = true;

    public bool IsFile => FilePath is not null;

    public MultipartPart Clone() => new()
    {
        Name = Name,
        Value = Value,
        FilePath = FilePath,
        FileContentAsValue = FileContentAsValue,
        FileName = FileName,
        ContentType = ContentType,
        Enabled = Enabled,
    };
}

/// <summary>Request body. Exactly one representation is meaningful, selected by <see cref="Mode"/>.</summary>
public sealed class RequestBody
{
    public BodyMode Mode { get; set; } = BodyMode.None;

    /// <summary>Body text for <see cref="BodyMode.Raw"/>, <see cref="BodyMode.Json"/> and <see cref="BodyMode.FormUrlEncoded"/>.</summary>
    public string? Text { get; set; }

    /// <summary>Raw bytes for a <see cref="BodyMode.Raw"/> body that is not valid text (for example a base64 HAR postData). Takes precedence over <see cref="Text"/>.</summary>
    public byte[]? Bytes { get; set; }

    public List<MultipartPart> Parts { get; set; } = [];

    /// <summary>Source file for <see cref="BodyMode.BinaryFile"/>.</summary>
    public string? FilePath { get; set; }

    public bool IsEmpty => Mode switch
    {
        BodyMode.None => true,
        BodyMode.Multipart => Parts.Count(p => p.Enabled) == 0,
        BodyMode.BinaryFile => string.IsNullOrEmpty(FilePath),
        _ => Bytes is null && Text is null,
    };

    public static RequestBody None() => new();

    public static RequestBody FromText(string text, BodyMode mode = BodyMode.Raw) => new() { Mode = mode, Text = text };

    public RequestBody Clone() => new()
    {
        Mode = Mode,
        Text = Text,
        Bytes = Bytes?.ToArray(),
        Parts = Parts.Select(p => p.Clone()).ToList(),
        FilePath = FilePath,
    };
}

public enum HttpVersionPreference
{
    /// <summary>Let the engine decide (HTTP/1.1, upgrading to HTTP/2 through ALPN when offered).</summary>
    Default,
    Http11,
    Http2,
}

public enum ConnectOverrideKind
{
    /// <summary>curl <c>--resolve host:port:address</c>.</summary>
    Resolve,

    /// <summary>curl <c>--connect-to host:port:connect-host:connect-port</c>.</summary>
    ConnectTo,
}

/// <summary>
/// Sends the TCP connection for <see cref="Host"/>:<see cref="Port"/> to a different address while the URL host
/// stays in the Host header and in SNI (SPEC 7.1).
/// </summary>
public sealed class ConnectOverride
{
    public ConnectOverrideKind Kind { get; set; } = ConnectOverrideKind.Resolve;

    /// <summary>URL host this override applies to. Empty matches any host (curl <c>--connect-to ::target:port</c>).</summary>
    public string Host { get; set; } = "";

    /// <summary>URL port this override applies to. 0 matches any port.</summary>
    public int Port { get; set; }

    /// <summary>Address or host name to connect to instead.</summary>
    public string TargetHost { get; set; } = "";

    /// <summary>Port to connect to instead. 0 keeps the URL port.</summary>
    public int TargetPort { get; set; }

    public bool Matches(string host, int port) =>
        (Host.Length == 0 || string.Equals(Host, host, StringComparison.OrdinalIgnoreCase)) &&
        (Port == 0 || Port == port);

    public ConnectOverride Clone() => new()
    {
        Kind = Kind,
        Host = Host,
        Port = Port,
        TargetHost = TargetHost,
        TargetPort = TargetPort,
    };

    public override string ToString() => Kind == ConnectOverrideKind.Resolve
        ? $"{Host}:{Port}:{TargetHost}"
        : $"{Host}:{(Port == 0 ? "" : Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}:{TargetHost}:{(TargetPort == 0 ? "" : TargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
}

public enum ClientCertificateSource
{
    PfxFile,
    PemFile,
    WindowsStore,
}

public sealed class ClientCertificateSpec
{
    public ClientCertificateSource Source { get; set; } = ClientCertificateSource.PfxFile;

    /// <summary>PFX or PEM certificate path.</summary>
    public string? Path { get; set; }

    public string? Password { get; set; }

    /// <summary>PEM private key path (curl <c>--key</c>). Optional when the key is in <see cref="Path"/>.</summary>
    public string? KeyPath { get; set; }

    /// <summary>Thumbprint of a certificate in the Windows store.</summary>
    public string? Thumbprint { get; set; }

    /// <summary>"CurrentUser" or "LocalMachine".</summary>
    public string StoreLocation { get; set; } = "CurrentUser";

    public ClientCertificateSpec Clone() => (ClientCertificateSpec)MemberwiseClone();
}

public sealed class RequestOptions
{
    public bool FollowRedirects { get; set; }

    /// <summary>curl's default is 50.</summary>
    public int MaxRedirects { get; set; } = 50;

    /// <summary>Whole-request timeout (curl <c>-m</c>). Null means no limit beyond cancellation.</summary>
    public TimeSpan? Timeout { get; set; }

    public TimeSpan? ConnectTimeout { get; set; }

    public bool AutoDecompress { get; set; } = true;

    /// <summary>Skip server certificate validation (curl <c>-k</c>).</summary>
    public bool Insecure { get; set; }

    public ClientCertificateSpec? ClientCertificate { get; set; }

    /// <summary>Proxy URI. Null means a direct connection; the system proxy is never used implicitly (SPEC 3.5).</summary>
    public string? Proxy { get; set; }

    public List<ConnectOverride> ConnectOverrides { get; set; } = [];

    public RequestOptions Clone() => new()
    {
        FollowRedirects = FollowRedirects,
        MaxRedirects = MaxRedirects,
        Timeout = Timeout,
        ConnectTimeout = ConnectTimeout,
        AutoDecompress = AutoDecompress,
        Insecure = Insecure,
        ClientCertificate = ClientCertificate?.Clone(),
        Proxy = Proxy,
        ConnectOverrides = ConnectOverrides.Select(o => o.Clone()).ToList(),
    };
}

/// <summary>The request model shared by the composer, the cURL parser and generators, and the request engine.</summary>
public sealed class HttpRequestSpec
{
    public string Method { get; set; } = "GET";

    public string Url { get; set; } = "";

    public HttpVersionPreference HttpVersion { get; set; } = HttpVersionPreference.Default;

    public List<HeaderEntry> Headers { get; set; } = [];

    public RequestBody Body { get; set; } = new();

    public RequestOptions Options { get; set; } = new();

    /// <summary>Identifies the HAR entry this request was built from, for replay diff and the replay guard. Not part of request equality.</summary>
    public string? OriginKey { get; set; }

    public IEnumerable<HeaderEntry> EnabledHeaders => Headers.Where(h => h.Enabled);

    public string? GetHeader(string name) =>
        Headers.FirstOrDefault(h => h.Enabled && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    public bool HasHeader(string name) => GetHeader(name) is not null;

    public void SetHeader(string name, string value)
    {
        var existing = Headers.FirstOrDefault(h => h.Enabled && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Headers.Add(new HeaderEntry(name, value));
        }
        else
        {
            existing.Value = value;
        }
    }

    public void RemoveHeader(string name) =>
        Headers.RemoveAll(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

    public HttpRequestSpec Clone() => new()
    {
        Method = Method,
        Url = Url,
        HttpVersion = HttpVersion,
        Headers = Headers.Select(h => h.Clone()).ToList(),
        Body = Body.Clone(),
        Options = Options.Clone(),
        OriginKey = OriginKey,
    };
}
