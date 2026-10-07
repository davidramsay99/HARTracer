namespace HarLens.Core.Compare;

public enum DiffKind
{
    Equal,
    Inserted,
    Deleted,
    Modified,
}

/// <summary>One side-by-side row. Line numbers are 1-based; a null side means the line exists only on the other side.</summary>
public sealed record DiffLine(DiffKind Kind, int? LeftNumber, string? Left, int? RightNumber, string? Right);

/// <summary>Line diff with Myers' O(ND) algorithm, trimmed of common prefix and suffix, paired for side-by-side display.</summary>
public static class LineDiff
{
    /// <summary>Beyond this many differences the diff falls back to positional pairing, to bound time and memory.</summary>
    public const int MaxEditDistance = 2_000;

    public static List<DiffLine> Compute(string? left, string? right) =>
        Compute(SplitLines(left), SplitLines(right));

    public static List<DiffLine> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var rows = new List<DiffLine>();
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
        {
            suffix++;
        }

        for (var i = 0; i < prefix; i++)
        {
            rows.Add(new DiffLine(DiffKind.Equal, i + 1, a[i], i + 1, b[i]));
        }

        var midA = Slice(a, prefix, a.Count - prefix - suffix);
        var midB = Slice(b, prefix, b.Count - prefix - suffix);
        var ops = Myers(midA, midB) ?? Positional(midA.Length, midB.Length);
        Pair(ops, midA, midB, prefix, rows);

        for (var i = 0; i < suffix; i++)
        {
            var ai = a.Count - suffix + i;
            var bi = b.Count - suffix + i;
            rows.Add(new DiffLine(DiffKind.Equal, ai + 1, a[ai], bi + 1, b[bi]));
        }

        return rows;
    }

    public static bool HasDifferences(IEnumerable<DiffLine> rows) => rows.Any(r => r.Kind != DiffKind.Equal);

    public static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.ReplaceLineEndings("\n").Split('\n').ToList();
    }

    private static string[] Slice(IReadOnlyList<string> list, int start, int count)
    {
        var result = new string[Math.Max(0, count)];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = list[start + i];
        }

        return result;
    }

    private enum Op : byte
    {
        Keep,
        Insert,
        Delete,
    }

    /// <summary>Returns the edit script, or null when the edit distance exceeds <see cref="MaxEditDistance"/>.</summary>
    private static List<Op>? Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0)
        {
            var simple = new List<Op>(n + m);
            simple.AddRange(Enumerable.Repeat(Op.Delete, n));
            simple.AddRange(Enumerable.Repeat(Op.Insert, m));
            return simple;
        }

        // Compare by hashed ids to make equality cheap.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var ha = a.Select(s => Id(ids, s)).ToArray();
        var hb = b.Select(s => Id(ids, s)).ToArray();

        var max = Math.Min(n + m, MaxEditDistance);
        var offset = max + 1;
        var v = new int[2 * max + 3];
        // trace[d] holds v[-d..d] as it was at the start of round d (compact: O(D^2) total).
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            trace.Add(v.AsSpan(offset - d, 2 * d + 1).ToArray());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                {
                    x = v[offset + k + 1];
                }
                else
                {
                    x = v[offset + k - 1] + 1;
                }

                var y = x - k;
                while (x < n && y < m && ha[x] == hb[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(trace, n, m, d);
                }
            }
        }

        return null;
    }

    private static List<Op> Backtrack(List<int[]> trace, int n, int m, int dFinal)
    {
        var ops = new List<Op>(n + m);
        int x = n, y = m;
        for (var d = dFinal; d > 0; d--)
        {
            var v = trace[d];
            int V(int k) => v[k + d];
            var k = x - y;
            var prevK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            var prevX = V(prevK);
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                ops.Add(Op.Keep);
                x--;
                y--;
            }

            if (x == prevX)
            {
                ops.Add(Op.Insert);
                y--;
            }
            else
            {
                ops.Add(Op.Delete);
                x--;
            }
        }

        while (x > 0 && y > 0)
        {
            ops.Add(Op.Keep);
            x--;
            y--;
        }

        ops.Reverse();
        return ops;
    }

    private static List<Op> Positional(int n, int m)
    {
        var ops = new List<Op>(n + m);
        var common = Math.Min(n, m);
        for (var i = 0; i < common; i++)
        {
            ops.Add(Op.Delete);
            ops.Add(Op.Insert);
        }

        ops.AddRange(Enumerable.Repeat(Op.Delete, n - common));
        ops.AddRange(Enumerable.Repeat(Op.Insert, m - common));
        return ops;
    }

    /// <summary>Turns the edit script into rows, pairing runs of deletions with following insertions as modifications.</summary>
    private static void Pair(List<Op> ops, string[] a, string[] b, int lineOffset, List<DiffLine> rows)
    {
        int ai = 0, bi = 0, i = 0;
        while (i < ops.Count)
        {
            if (ops[i] == Op.Keep)
            {
                rows.Add(new DiffLine(DiffKind.Equal, lineOffset + ai + 1, a[ai], lineOffset + bi + 1, b[bi]));
                ai++;
                bi++;
                i++;
                continue;
            }

            var deletes = new List<int>();
            var inserts = new List<int>();
            while (i < ops.Count && ops[i] != Op.Keep)
            {
                if (ops[i] == Op.Delete)
                {
                    deletes.Add(ai++);
                }
                else
                {
                    inserts.Add(bi++);
                }

                i++;
            }

            var paired = Math.Min(deletes.Count, inserts.Count);
            for (var p = 0; p < paired; p++)
            {
                rows.Add(new DiffLine(DiffKind.Modified, lineOffset + deletes[p] + 1, a[deletes[p]], lineOffset + inserts[p] + 1, b[inserts[p]]));
            }

            foreach (var d in deletes.Skip(paired))
            {
                rows.Add(new DiffLine(DiffKind.Deleted, lineOffset + d + 1, a[d], null, null));
            }

            foreach (var ins in inserts.Skip(paired))
            {
                rows.Add(new DiffLine(DiffKind.Inserted, null, null, lineOffset + ins + 1, b[ins]));
            }
        }
    }

    private static int Id(Dictionary<string, int> ids, string s)
    {
        if (!ids.TryGetValue(s, out var id))
        {
            id = ids.Count;
            ids[s] = id;
        }

        return id;
    }
}
