using Harborer.Core.Har;
using Harborer.Core.Http;

namespace Harborer.Core.Composer;

public enum CredentialLocation
{
    Header,
    QueryParameter,
}

public sealed record CredentialFinding(CredentialLocation Location, string Name, string MaskedValue)
{
    public string Description => Location == CredentialLocation.Header
        ? $"{Name} header (captured value {MaskedValue})"
        : $"'{Name}' query parameter (captured value {MaskedValue})";
}

/// <summary>
/// Captured requests carry live credentials. Before sending a request that originates from a HAR entry,
/// the composer asks for confirmation when the captured Authorization, Proxy-Authorization, Cookie or a known
/// token query parameter is still present with its captured value.
/// </summary>
public static class ReplayGuard
{
    public static readonly IReadOnlySet<string> CredentialHeaders =
        new HashSet<string>(["Authorization", "Proxy-Authorization", "Cookie"], StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> TokenQueryParameters = new HashSet<string>(
        [
            "access_token", "id_token", "refresh_token", "code", "token", "client_secret", "password", "sig", "signature",
            "SAMLResponse", "SAMLRequest", "api_key", "apikey", "api-key", "x-api-key", "session", "sessionid", "auth",
            "jwt", "assertion", "ticket",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static List<CredentialFinding> Inspect(HttpRequestSpec request, HarEntry? origin)
    {
        var findings = new List<CredentialFinding>();
        if (origin is null)
        {
            return findings;
        }

        foreach (var header in request.EnabledHeaders)
        {
            if (!CredentialHeaders.Contains(header.Name))
            {
                continue;
            }

            foreach (var captured in origin.RequestHeaders)
            {
                if (captured.Name.Equals(header.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(captured.Value, header.Value, StringComparison.Ordinal))
                {
                    findings.Add(new CredentialFinding(CredentialLocation.Header, header.Name, Sanitize.SecretMasker.Mask(header.Value)));
                    break;
                }
            }
        }

        var capturedParams = FormUrlEncoding.ParseQuery(origin.Url);
        foreach (var p in FormUrlEncoding.ParseQuery(request.Url))
        {
            if (TokenQueryParameters.Contains(p.Name) && p.Value.Length > 0 &&
                capturedParams.Any(c => c.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && c.Value == p.Value))
            {
                findings.Add(new CredentialFinding(CredentialLocation.QueryParameter, p.Name, Sanitize.SecretMasker.Mask(p.Value)));
            }
        }

        return findings;
    }

    /// <summary>Returns a copy without the flagged headers and query parameters ("strip credentials and send").</summary>
    public static HttpRequestSpec StripCredentials(HttpRequestSpec request, IEnumerable<CredentialFinding> findings)
    {
        var copy = request.Clone();
        var list = findings.ToList();
        foreach (var f in list.Where(f => f.Location == CredentialLocation.Header))
        {
            copy.RemoveHeader(f.Name);
        }

        var queryNames = new HashSet<string>(list.Where(f => f.Location == CredentialLocation.QueryParameter).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        if (queryNames.Count > 0)
        {
            var raw = FormUrlEncoding.RawQuery(copy.Url);
            var kept = raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !queryNames.Contains(FormUrlEncoding.Decode(part.Split('=')[0])));
            copy.Url = FormUrlEncoding.WithQuery(copy.Url, string.Join("&", kept));
        }

        return copy;
    }
}

/// <summary>Hosts for which the replay prompt is suppressed. Lives for the current run only; never persisted.</summary>
public sealed class ReplayGuardSuppressions
{
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSuppressed(string host) => _hosts.Contains(host);

    public void Suppress(string host) => _hosts.Add(host);

    public void Clear() => _hosts.Clear();
}
