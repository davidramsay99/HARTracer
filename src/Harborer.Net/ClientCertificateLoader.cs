using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harborer.Core.Http;

namespace Harborer.Net;

/// <summary>Loads the client certificate (curl -E / --cert, --key) before any connection is opened.</summary>
internal static class ClientCertificateLoader
{
    public static X509Certificate2 Load(ClientCertificateSpec spec, string? baseDirectory)
    {
        X509Certificate2 certificate = spec.Source switch
        {
            ClientCertificateSource.PfxFile => LoadPfx(spec, baseDirectory),
            ClientCertificateSource.PemFile => LoadPem(spec, baseDirectory),
            ClientCertificateSource.WindowsStore => LoadFromStore(spec),
            _ => throw new RequestPreparationException($"Unknown client certificate source '{spec.Source}'"),
        };

        if (!certificate.HasPrivateKey)
        {
            string subject = certificate.Subject;
            certificate.Dispose();
            throw new RequestPreparationException($"The client certificate '{subject}' has no private key");
        }

        return certificate;
    }

    private static X509Certificate2 LoadPfx(ClientCertificateSpec spec, string? baseDirectory)
    {
        string path = Preflight.ResolvePath(spec.Path, baseDirectory, "the client certificate");
        if (!File.Exists(path))
        {
            throw new RequestPreparationException($"Client certificate file not found: {path}");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, spec.Password);
        }
        catch (CryptographicException ex)
        {
            throw new RequestPreparationException($"Could not load the client certificate {path}: {ex.Message}", ex);
        }
    }

    private static X509Certificate2 LoadPem(ClientCertificateSpec spec, string? baseDirectory)
    {
        string path = Preflight.ResolvePath(spec.Path, baseDirectory, "the client certificate");
        string? keyPath = string.IsNullOrWhiteSpace(spec.KeyPath)
            ? null
            : Preflight.ResolvePath(spec.KeyPath, baseDirectory, "the client certificate key");
        foreach (string file in keyPath is null ? [path] : new[] { path, keyPath })
        {
            if (!File.Exists(file))
            {
                throw new RequestPreparationException($"Client certificate file not found: {file}");
            }
        }

        try
        {
            using var pem = string.IsNullOrEmpty(spec.Password)
                ? X509Certificate2.CreateFromPemFile(path, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(path, spec.Password, keyPath);

            // A PEM key is ephemeral; Windows SChannel can only use a key that came from a PKCS#12 import.
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new RequestPreparationException($"Could not load the client certificate {path}: {ex.Message}", ex);
        }
    }

    private static X509Certificate2 LoadFromStore(ClientCertificateSpec spec)
    {
        string thumbprint = new((spec.Thumbprint ?? "").Where(char.IsAsciiHexDigit).ToArray());
        if (thumbprint.Length == 0)
        {
            throw new RequestPreparationException("No thumbprint given for the client certificate");
        }

        if (!Enum.TryParse<StoreLocation>(spec.StoreLocation, ignoreCase: true, out var location))
        {
            throw new RequestPreparationException(
                $"Unknown certificate store location '{spec.StoreLocation}': use CurrentUser or LocalMachine");
        }

        try
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count == 0)
            {
                throw new RequestPreparationException(
                    $"No certificate with thumbprint {thumbprint} in the {location} personal store");
            }

            for (int i = 1; i < found.Count; i++)
            {
                found[i].Dispose();
            }

            return found[0];
        }
        catch (CryptographicException ex)
        {
            throw new RequestPreparationException($"Could not open the {location} personal certificate store: {ex.Message}", ex);
        }
    }
}
