using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HarLens.Core.Engine;
using HarLens.Core.Http;

namespace HarLens.Net.Tests;

/// <summary>Self-signed certificates made at test time; nothing is installed in any store.</summary>
internal static class TestCertificates
{
    private static readonly Lazy<X509Certificate2> s_server = new(CreateServer);
    private static readonly Lazy<X509Certificate2> s_client = new(CreateClient);

    /// <summary>CN=example.test with SAN DNS example.test, DNS localhost and IP 127.0.0.1.</summary>
    public static X509Certificate2 Server => s_server.Value;

    /// <summary>CN=HarLens Test Client, client authentication EKU.</summary>
    public static X509Certificate2 Client => s_client.Value;

    public static string WriteClientPfx(string directory, string password)
    {
        string path = Path.Combine(directory, "client.pfx");
        File.WriteAllBytes(path, Client.Export(X509ContentType.Pkcs12, password));
        return path;
    }

    private static X509Certificate2 CreateServer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=example.test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("example.test");
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        return Persist(request);
    }

    private static X509Certificate2 CreateClient()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=HarLens Test Client", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        return Persist(request);
    }

    // A PKCS#12 round trip gives a key that SslStream can use on every platform. Exportable, because the tests
    // write the client certificate out again as PFX and PEM, and Windows CNG enforces the export policy.
    private static X509Certificate2 Persist(CertificateRequest request)
    {
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }
}

/// <summary>A temporary directory removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("harlens-net-tests-").FullName;
    }

    public string Path { get; }

    public string Write(string name, byte[] content)
    {
        string path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string Write(string name, string content) => Write(name, Encoding.UTF8.GetBytes(content));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Send
{
    public static HttpRequestSpec Get(string url, params (string Name, string Value)[] headers)
    {
        var spec = new HttpRequestSpec { Method = "GET", Url = url };
        foreach (var (name, value) in headers)
        {
            spec.Headers.Add(new HeaderEntry(name, value));
        }

        return spec;
    }

    public static Task<SendOutcome> RunAsync(HttpRequestSpec spec, SendSettings? settings = null, CancellationToken cancellationToken = default) =>
        new RequestEngine().SendAsync(spec, settings ?? new SendSettings(), cancellationToken);

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
