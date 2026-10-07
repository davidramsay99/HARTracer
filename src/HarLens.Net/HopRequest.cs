using HarLens.Core.Http;
using HarLens.Core.Model;

namespace HarLens.Net;

/// <summary>One request on the redirect chain: the original send, or a hop derived from a 3xx response.</summary>
internal sealed record HopRequest(
    string Method,
    Uri Uri,
    IReadOnlyList<HarHeader> Headers,
    PreparedBody? Body,
    HttpVersionPreference Version,
    bool StripCredentials,
    IReadOnlyList<string> Notices)
{
    public static HopRequest First(HttpRequestSpec spec, Uri uri, PreparedBody? body)
    {
        var notices = new List<string>();
        if (spec.HttpVersion == HttpVersionPreference.Http2 && !Preflight.IsHttps(uri))
        {
            notices.Add("HTTP/2 over cleartext (h2c) is not supported; the request was sent as HTTP/1.1");
        }

        if (!string.IsNullOrWhiteSpace(spec.Options.Proxy) && spec.Options.ConnectOverrides.Count > 0)
        {
            notices.Add("Connect overrides are not applied when a proxy is set; the proxy connects to the URL host");
        }

        var headers = spec.EnabledHeaders
            .Where(h => h.Name.Trim().Length > 0)
            .Select(h => new HarHeader(h.Name.Trim(), h.Value))
            .ToList();
        return new HopRequest(Preflight.NormalizeMethod(spec.Method), uri, headers, body, spec.HttpVersion, false, notices);
    }
}

/// <summary>curl -L semantics for following redirects one hop at a time.</summary>
internal static class RedirectPolicy
{
    // Headers that describe a body; dropped together with the body when a redirect switches to GET (Fetch spec).
    private static readonly HashSet<string> s_bodyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language", "Content-Location", "Content-Length",
    };

    public static bool IsFollowable(int status) => status is 300 or 301 or 302 or 303 or 307 or 308;

    public static bool IsCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the next hop. Credentials are compared against the original URL, like curl without --location-trusted.</summary>
    public static HopRequest Next(HopRequest current, Uri originalUri, int status, string location)
    {
        if (!Uri.TryCreate(current.Uri, location.Trim(), out var target) || !target.IsAbsoluteUri)
        {
            throw new RequestPreparationException($"Cannot follow the redirect: invalid Location '{location}'");
        }

        if (!Preflight.IsHttpScheme(target.Scheme) || target.Host.Length == 0)
        {
            throw new RequestPreparationException(
                $"Cannot follow the redirect to '{location}': protocol \"{target.Scheme}\" not supported");
        }

        var notices = new List<string>();
        string method = current.Method;
        var body = current.Body;
        IReadOnlyList<HarHeader> headers = current.Headers;

        bool switchToGet = status switch
        {
            301 or 302 => method.Equals("POST", StringComparison.OrdinalIgnoreCase),
            303 => !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        if (switchToGet)
        {
            bool hadBody = body is not null || headers.Any(h => s_bodyHeaders.Contains(h.Name));
            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) || hadBody)
            {
                notices.Add($"{status} redirect: {method} changed to GET" + (hadBody ? " and the body dropped" : ""));
            }

            method = "GET";
            body = null;
            headers = headers.Where(h => !s_bodyHeaders.Contains(h.Name)).ToList();
        }

        bool strip = !SameOrigin(originalUri, target);
        if (strip && headers.Any(h => IsCredentialHeader(h.Name)))
        {
            notices.Add("Authorization and Cookie headers were not sent: the redirect leaves the original host (curl without --location-trusted)");
        }

        return new HopRequest(method, target, headers, body, current.Version, strip, notices);
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port;
}
