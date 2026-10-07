using HarLens.Core.Har;
using HarLens.Core.Http;
using HarLens.Core.Model;
using HarLens.Core.Text;

namespace HarLens.Core.Compare;

/// <summary>A field of the request or status line compared side by side.</summary>
public sealed record FieldDiff(string Field, string Left, string Right)
{
    public bool Same => string.Equals(Left, Right, StringComparison.Ordinal);
}

/// <summary>Headers matched by name, case-insensitively (SPEC 6.6). Repeated headers are joined with a newline.</summary>
public sealed record HeaderDiff(string Name, string? Left, string? Right)
{
    public DiffKind Kind => Left is null ? DiffKind.Inserted
        : Right is null ? DiffKind.Deleted
        : string.Equals(Left, Right, StringComparison.Ordinal) ? DiffKind.Equal
        : DiffKind.Modified;
}

/// <summary>Side-by-side comparison of two entries: request line, headers, bodies (SPEC 6.6, also original versus replay).</summary>
public sealed class EntryComparison
{
    public required HarEntry Left { get; init; }

    public required HarEntry Right { get; init; }

    public List<FieldDiff> Summary { get; } = [];

    public List<HeaderDiff> RequestHeaders { get; init; } = [];

    public List<HeaderDiff> ResponseHeaders { get; init; } = [];

    public List<DiffLine> RequestBody { get; init; } = [];

    public List<DiffLine> ResponseBody { get; init; } = [];

    /// <summary>True when the bodies were JSON on both sides and were pretty-printed before diffing.</summary>
    public bool RequestBodyPrettyPrinted { get; init; }

    public bool ResponseBodyPrettyPrinted { get; init; }

    public bool HasDifferences =>
        Summary.Any(s => !s.Same) || RequestHeaders.Any(h => h.Kind != DiffKind.Equal) ||
        ResponseHeaders.Any(h => h.Kind != DiffKind.Equal) || LineDiff.HasDifferences(RequestBody) || LineDiff.HasDifferences(ResponseBody);

    public static EntryComparison Compare(HarEntry left, HarEntry right, BodyCache? cache = null)
    {
        DecodedBody? Body(HarEntry e, BodySide side) => cache is null ? BodyReader.Read(e, side) : cache.Get(e, side);

        var (requestBody, requestPretty) = DiffBodies(Body(left, BodySide.Request), Body(right, BodySide.Request));
        var (responseBody, responsePretty) = DiffBodies(Body(left, BodySide.Response), Body(right, BodySide.Response));
        var comparison = new EntryComparison
        {
            Left = left,
            Right = right,
            RequestHeaders = DiffHeaders(left.RequestHeaders, right.RequestHeaders),
            ResponseHeaders = DiffHeaders(left.ResponseHeaders, right.ResponseHeaders),
            RequestBody = requestBody,
            ResponseBody = responseBody,
            RequestBodyPrettyPrinted = requestPretty,
            ResponseBodyPrettyPrinted = responsePretty,
        };

        comparison.Summary.Add(new FieldDiff("Method", left.Method, right.Method));
        comparison.Summary.Add(new FieldDiff("URL", left.Url, right.Url));
        comparison.Summary.Add(new FieldDiff("Request version", RawMessage.Http1Version(left.RequestHttpVersion), RawMessage.Http1Version(right.RequestHttpVersion)));
        comparison.Summary.Add(new FieldDiff("Status", Status(left), Status(right)));
        comparison.Summary.Add(new FieldDiff("Response version", RawMessage.Http1Version(left.ResponseHttpVersion), RawMessage.Http1Version(right.ResponseHttpVersion)));
        comparison.Summary.Add(new FieldDiff("MIME type", left.MimeType, right.MimeType));
        comparison.Summary.Add(new FieldDiff("Remote address", left.ServerIPAddress ?? "", right.ServerIPAddress ?? ""));
        return comparison;
    }

    public static List<HeaderDiff> DiffHeaders(IReadOnlyList<HarHeader> left, IReadOnlyList<HarHeader> right)
    {
        static Dictionary<string, (string Display, List<string> Values)> Group(IReadOnlyList<HarHeader> headers, List<string> order)
        {
            var map = new Dictionary<string, (string, List<string>)>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
            {
                if (!map.TryGetValue(h.Name, out var entry))
                {
                    entry = (h.Name, []);
                    map[h.Name] = entry;
                    if (!order.Contains(h.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        order.Add(h.Name);
                    }
                }

                entry.Item2.Add(h.Value);
            }

            return map;
        }

        var order = new List<string>();
        var l = Group(left, order);
        var r = Group(right, order);
        var rows = new List<HeaderDiff>();
        foreach (var name in order)
        {
            var hasLeft = l.TryGetValue(name, out var lv);
            var hasRight = r.TryGetValue(name, out var rv);
            rows.Add(new HeaderDiff(hasLeft ? lv.Display : rv.Display,
                hasLeft ? string.Join("\n", lv.Values) : null,
                hasRight ? string.Join("\n", rv.Values) : null));
        }

        return rows;
    }

    /// <summary>Line diff of two bodies; JSON on both sides is pretty-printed first. Binary bodies compare as a single summary line.</summary>
    public static (List<DiffLine> Rows, bool PrettyPrinted) DiffBodies(DecodedBody? left, DecodedBody? right)
    {
        if (left is { IsText: false } || right is { IsText: false })
        {
            var a = left?.Bytes ?? [];
            var b = right?.Bytes ?? [];
            var same = a.AsSpan().SequenceEqual(b);
            var ls = left is null ? "(no body)" : $"[{a.Length:N0} bytes{(left.IsText ? " text" : " binary")}]";
            var rs = right is null ? "(no body)" : $"[{b.Length:N0} bytes{(right.IsText ? " text" : " binary")}]";
            return ([new DiffLine(same ? DiffKind.Equal : DiffKind.Modified, 1, ls, 1, rs)], false);
        }

        var leftText = left?.Text ?? "";
        var rightText = right?.Text ?? "";
        var pretty = false;
        if (JsonPretty.TryFormat(leftText, out var lp) && JsonPretty.TryFormat(rightText, out var rp))
        {
            leftText = lp;
            rightText = rp;
            pretty = true;
        }

        return (LineDiff.Compute(leftText, rightText), pretty);
    }

    private static string Status(HarEntry e) => e.Status == 0 ? $"(failed){(e.Error is null ? "" : " " + e.Error)}" : $"{e.Status} {e.StatusText}".Trim();
}
