using System.Globalization;

namespace HarLens.Core.Http;

/// <summary>
/// Semantic equality for <see cref="HttpRequestSpec"/>, used by the cURL round-trip rule (SPEC 7.3).
/// Two requests are equivalent when they would put the same request on the wire with the same options.
/// The body editing mode (raw, JSON, form grid) is a UI hint and is not compared; the body content is.
/// Disabled headers are not compared because they are never sent or exported.
/// </summary>
public static class RequestEquivalence
{
    public static bool AreEquivalent(HttpRequestSpec a, HttpRequestSpec b) => Differences(a, b).Count == 0;

    public static IReadOnlyList<string> Differences(HttpRequestSpec a, HttpRequestSpec b)
    {
        var diffs = new List<string>();
        void Check<T>(string what, T x, T y)
        {
            if (!EqualityComparer<T>.Default.Equals(x, y))
            {
                diffs.Add($"{what}: '{x}' != '{y}'");
            }
        }

        Check("method", a.Method, b.Method);
        Check("url", a.Url, b.Url);
        Check("http version", a.HttpVersion, b.HttpVersion);

        var ha = a.EnabledHeaders.ToList();
        var hb = b.EnabledHeaders.ToList();
        if (ha.Count != hb.Count)
        {
            diffs.Add($"header count: {ha.Count} != {hb.Count} ([{string.Join(", ", ha.Select(h => h.Name))}] vs [{string.Join(", ", hb.Select(h => h.Name))}])");
        }
        else
        {
            for (var i = 0; i < ha.Count; i++)
            {
                Check($"header[{i}].name", ha[i].Name, hb[i].Name);
                Check($"header[{i}].value", ha[i].Value, hb[i].Value);
            }
        }

        CompareBody(a.Body, b.Body, diffs);
        CompareOptions(a.Options, b.Options, diffs);
        return diffs;
    }

    private static void CompareBody(RequestBody a, RequestBody b, List<string> diffs)
    {
        var ka = Kind(a);
        var kb = Kind(b);
        if (ka != kb)
        {
            diffs.Add($"body kind: {ka} != {kb}");
            return;
        }

        switch (ka)
        {
            case "text":
                var ta = a.Bytes ?? System.Text.Encoding.UTF8.GetBytes(a.Text ?? "");
                var tb = b.Bytes ?? System.Text.Encoding.UTF8.GetBytes(b.Text ?? "");
                if (!ta.AsSpan().SequenceEqual(tb))
                {
                    diffs.Add($"body text differs: '{Preview(a)}' != '{Preview(b)}'");
                }

                break;
            case "multipart":
                var pa = a.Parts.Where(p => p.Enabled).ToList();
                var pb = b.Parts.Where(p => p.Enabled).ToList();
                if (pa.Count != pb.Count)
                {
                    diffs.Add($"multipart part count: {pa.Count} != {pb.Count}");
                    return;
                }

                for (var i = 0; i < pa.Count; i++)
                {
                    var x = pa[i];
                    var y = pb[i];
                    if (x.Name != y.Name || x.Value != y.Value || x.FilePath != y.FilePath ||
                        x.FileContentAsValue != y.FileContentAsValue || x.ContentType != y.ContentType ||
                        EffectiveFileName(x) != EffectiveFileName(y))
                    {
                        diffs.Add($"multipart part {i} differs: {Describe(x)} != {Describe(y)}");
                    }
                }

                break;
            case "file":
                if (a.FilePath != b.FilePath)
                {
                    diffs.Add($"body file: '{a.FilePath}' != '{b.FilePath}'");
                }

                break;
        }
    }

    private static string? EffectiveFileName(MultipartPart p) =>
        p.FileName ?? (p.FilePath is not null && !p.FileContentAsValue ? Path.GetFileName(p.FilePath) : null);

    private static string Describe(MultipartPart p) =>
        $"[{p.Name} value={p.Value} file={p.FilePath} asValue={p.FileContentAsValue} filename={EffectiveFileName(p)} type={p.ContentType}]";

    private static string Kind(RequestBody body) => body.Mode switch
    {
        BodyMode.None => "none",
        BodyMode.Multipart => body.Parts.Any(p => p.Enabled) ? "multipart" : "none",
        BodyMode.BinaryFile => string.IsNullOrEmpty(body.FilePath) ? "none" : "file",
        _ => body.Bytes is null && body.Text is null ? "none" : "text",
    };

    private static string Preview(RequestBody body)
    {
        var text = body.Bytes is not null ? Convert.ToBase64String(body.Bytes) : body.Text ?? "";
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    private static void CompareOptions(RequestOptions a, RequestOptions b, List<string> diffs)
    {
        void Check<T>(string what, T x, T y)
        {
            if (!EqualityComparer<T>.Default.Equals(x, y))
            {
                diffs.Add($"option {what}: '{x}' != '{y}'");
            }
        }

        Check("follow redirects", a.FollowRedirects, b.FollowRedirects);
        if (a.FollowRedirects || b.FollowRedirects)
        {
            Check("max redirects", a.MaxRedirects, b.MaxRedirects);
        }

        Check("timeout", a.Timeout, b.Timeout);
        Check("connect timeout", a.ConnectTimeout, b.ConnectTimeout);
        Check("auto decompress", a.AutoDecompress, b.AutoDecompress);
        Check("insecure", a.Insecure, b.Insecure);
        Check("proxy", a.Proxy, b.Proxy);
        Check("client certificate", CertKey(a.ClientCertificate), CertKey(b.ClientCertificate));
        Check("connect overrides",
            string.Join(" ", a.ConnectOverrides.Select(o => o.Kind + "=" + o)),
            string.Join(" ", b.ConnectOverrides.Select(o => o.Kind + "=" + o)));
    }

    private static string CertKey(ClientCertificateSpec? c) => c is null
        ? ""
        : string.Create(CultureInfo.InvariantCulture, $"{c.Source}|{c.Path}|{c.Password}|{c.KeyPath}|{c.Thumbprint}|{c.StoreLocation}");
}
