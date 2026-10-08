using System.Globalization;
using System.Net.Http.Headers;
using Harborer.Core.Model;

namespace Harborer.Net;

/// <summary>
/// Header lists taken from the .NET message objects, used for HTTP/2 (whose HPACK frames are not decoded) and when
/// the wire record holds no complete head. HTTP/2 names are lower case, as on the wire.
/// </summary>
internal static class HeaderCollector
{
    // Connection-specific headers that the HTTP/2 layer never sends.
    private static readonly HashSet<string> s_notSentOverHttp2 = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Upgrade", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding",
    };

    public static List<HarHeader> Request(HttpRequestMessage message, bool http2)
    {
        var list = new List<HarHeader>();
        var uri = message.RequestUri!;
        string? host = First(message.Headers.NonValidated, "Host");
        string authority = host ?? (uri.IsDefaultPort
            ? uri.IdnHost
            : uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture));

        if (http2)
        {
            list.Add(new HarHeader(":method", message.Method.Method));
            list.Add(new HarHeader(":authority", authority));
            list.Add(new HarHeader(":scheme", uri.Scheme));
            list.Add(new HarHeader(":path", uri.PathAndQuery));
        }
        else if (host is null)
        {
            list.Add(new HarHeader("Host", authority));
        }

        Append(list, message.Headers.NonValidated, http2);
        if (message.Content is { } content)
        {
            Append(list, content.Headers.NonValidated, http2);
            if (!list.Any(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) &&
                content.Headers.ContentLength is long length)
            {
                list.Add(new HarHeader(http2 ? "content-length" : "Content-Length", length.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return list;
    }

    public static List<HarHeader> Response(HttpResponseMessage response, bool http2)
    {
        var list = new List<HarHeader>();
        Append(list, response.Headers.NonValidated, http2);
        Append(list, response.Content.Headers.NonValidated, http2);
        list.AddRange(Trailers(response, http2));
        return list;
    }

    public static List<HarHeader> Trailers(HttpResponseMessage response, bool http2)
    {
        var list = new List<HarHeader>();
        Append(list, response.TrailingHeaders.NonValidated, http2);
        return list;
    }

    public static IEnumerable<string> Values(HttpHeadersNonValidated headers, string name) =>
        headers.TryGetValues(name, out var values) ? values : [];

    private static string? First(HttpHeadersNonValidated headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static void Append(List<HarHeader> list, HttpHeadersNonValidated headers, bool http2)
    {
        foreach (var header in headers)
        {
            if (http2 && s_notSentOverHttp2.Contains(header.Key))
            {
                continue;
            }

            string name = http2 ? header.Key.ToLowerInvariant() : header.Key;
            foreach (string value in header.Value)
            {
                if (http2 && name == "te" && !value.Equals("trailers", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new HarHeader(name, value));
            }
        }
    }
}
