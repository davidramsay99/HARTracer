using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HarLens.Core.Engine;

namespace HarLens.Net;

/// <summary>Reads server certificate and TLS session details into <see cref="TlsDetails"/>, and words validation failures like curl.</summary>
internal static class CertificateInspector
{
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    public static void FillCertificate(TlsDetails tls, X509Certificate certificate)
    {
        if (certificate is X509Certificate2 cert)
        {
            Fill(tls, cert);
            return;
        }

        using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        Fill(tls, copy);
    }

    public static void FillSession(TlsDetails tls, SslStream ssl)
    {
        tls.Protocol = ProtocolName(ssl.SslProtocol);
        try
        {
            tls.CipherSuite = ssl.NegotiatedCipherSuite.ToString();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            tls.CipherSuite = "";
        }

        string alpn = ssl.NegotiatedApplicationProtocol.ToString();
        tls.ApplicationProtocol = string.IsNullOrEmpty(alpn) ? null : alpn;
        tls.ServerName ??= SniName(ssl.TargetHostName);
        if (string.IsNullOrEmpty(tls.Subject) && ssl.RemoteCertificate is { } remote)
        {
            FillCertificate(tls, remote);
        }
    }

    /// <summary>The SNI value sent for a target host: none for IP literals.</summary>
    public static string? SniName(string? targetHost) =>
        string.IsNullOrEmpty(targetHost) || IPAddress.TryParse(targetHost.Trim('[', ']'), out _) ? null : targetHost;

    public static string ProtocolName(SslProtocols protocol) => protocol.ToString() switch
    {
        "Tls13" => "TLS 1.3",
        "Tls12" => "TLS 1.2",
        "Tls11" => "TLS 1.1",
        "Tls" => "TLS 1.0",
        "Ssl3" => "SSL 3.0",
        "Ssl2" => "SSL 2.0",
        "None" => "",
        var other => other,
    };

    public static List<string> SubjectAlternativeNames(X509Certificate2 certificate)
    {
        var names = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != SubjectAlternativeNameOid)
            {
                continue;
            }

            try
            {
                var san = extension as X509SubjectAlternativeNameExtension
                    ?? new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
                names.AddRange(san.EnumerateDnsNames().Select(n => "DNS:" + n));
                names.AddRange(san.EnumerateIPAddresses().Select(a => "IP:" + a));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                names.Add("(unreadable subjectAltName extension)");
            }
        }

        return names;
    }

    public static string DescribePolicyErrors(SslPolicyErrors errors, X509Chain? chain)
    {
        if (errors == SslPolicyErrors.None)
        {
            return "None";
        }

        var statuses = ChainStatuses(chain);
        return statuses.Count == 0 ? errors.ToString() : $"{errors} ({string.Join(", ", statuses)})";
    }

    /// <summary>curl-style wording of a rejected server certificate.</summary>
    public static string FailureMessage(SslPolicyErrors errors, X509Chain? chain, X509Certificate? certificate, string targetHost)
    {
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return "SSL: the server presented no certificate";
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var statuses = ChainStatuses(chain);
            string problem;
            if (statuses.Contains(X509ChainStatusFlags.NotTimeValid))
            {
                problem = certificate is X509Certificate2 c && c.NotBefore > DateTime.Now
                    ? "certificate is not yet valid"
                    : "certificate has expired";
            }
            else if (statuses.Contains(X509ChainStatusFlags.Revoked))
            {
                problem = "certificate revoked";
            }
            else if (statuses.Contains(X509ChainStatusFlags.UntrustedRoot))
            {
                problem = certificate is not null && certificate.Subject == certificate.Issuer
                    ? "self-signed certificate"
                    : "self-signed certificate in certificate chain";
            }
            else if (statuses.Contains(X509ChainStatusFlags.PartialChain))
            {
                problem = "unable to get local issuer certificate";
            }
            else
            {
                problem = statuses.Count == 0 ? "the certificate chain is not trusted" : string.Join(", ", statuses);
            }

            return $"SSL certificate problem: {problem}";
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return $"SSL: no alternative certificate subject name matches target host name '{targetHost}'";
        }

        return $"SSL certificate problem: {errors}";
    }

    private static void Fill(TlsDetails tls, X509Certificate2 cert)
    {
        tls.Subject = cert.Subject;
        tls.Issuer = cert.Issuer;
        tls.NotBefore = new DateTimeOffset(cert.NotBefore);
        tls.NotAfter = new DateTimeOffset(cert.NotAfter);
        tls.Thumbprint = cert.Thumbprint;
        tls.SubjectAlternativeNames = SubjectAlternativeNames(cert);
    }

    private static List<X509ChainStatusFlags> ChainStatuses(X509Chain? chain) =>
        chain?.ChainStatus
            .Select(s => s.Status)
            .Where(s => s != X509ChainStatusFlags.NoError)
            .Distinct()
            .ToList() ?? [];
}
