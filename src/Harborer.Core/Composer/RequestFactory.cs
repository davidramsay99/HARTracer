using Harborer.Core.Har;
using Harborer.Core.Http;
using Harborer.Core.Model;
using Harborer.Core.Text;

namespace Harborer.Core.Composer;

/// <summary>Builds composer requests from captured entries ("Send to Composer", "Copy as cURL").</summary>
public static class RequestFactory
{
    /// <summary>
    /// Converts an entry to a request. Pseudo-headers and Content-Length are dropped; a Host header equal
    /// to the URL authority is dropped too so that changing the URL retargets the request, while a different Host is kept.
    /// </summary>
    public static HttpRequestSpec FromEntry(HarEntry entry, DecodedBody? requestBody = null)
    {
        var url = entry.Url;
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            url = url[..hash];
        }

        var spec = new HttpRequestSpec
        {
            Method = string.IsNullOrEmpty(entry.Method) ? "GET" : entry.Method,
            Url = url,
            HttpVersion = MapVersion(entry.RequestHttpVersion),
            OriginKey = entry.Key,
        };
        spec.Options.AutoDecompress = true;

        var authority = entry.HostDisplay;
        foreach (var header in entry.RequestHeaders)
        {
            if (header.Name.StartsWith(':') || header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(header.Value, authority, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            spec.Headers.Add(new HeaderEntry(header.Name, header.Value));
        }

        requestBody ??= entry.RequestBody.Exists ? BodyReader.Read(entry, BodySide.Request) : null;
        var contentType = spec.GetHeader("Content-Type") ?? entry.RequestMimeType;
        if (requestBody is not null && requestBody.Bytes.Length > 0)
        {
            spec.Body = requestBody.IsText
                ? RequestBody.FromText(requestBody.Text!, ModeFor(contentType))
                : new RequestBody { Mode = BodyMode.Raw, Bytes = requestBody.Bytes };
        }
        else if (entry.HasPostParams)
        {
            using var detail = EntryDetail.Load(entry);
            spec.Body = RequestBody.FromText(FormUrlEncoding.Serialize(detail.PostParams), BodyMode.FormUrlEncoded);
        }

        return spec;
    }

    public static BodyMode ModeFor(string? contentType) =>
        MimeTypes.IsJson(contentType) ? BodyMode.Json
        : MimeTypes.IsFormUrlEncoded(contentType) ? BodyMode.FormUrlEncoded
        : BodyMode.Raw;

    public static HttpVersionPreference MapVersion(string? version)
    {
        var v = (version ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "http/1.1" or "http/1.0" => HttpVersionPreference.Http11,
            "h2" or "http/2" or "http/2.0" => HttpVersionPreference.Http2,
            _ => HttpVersionPreference.Default,
        };
    }

    /// <summary>Headers of a request as name/value pairs (enabled rows only).</summary>
    public static List<HarHeader> EnabledHeaders(HttpRequestSpec spec) =>
        spec.EnabledHeaders.Select(h => new HarHeader(h.Name, h.Value)).ToList();
}
