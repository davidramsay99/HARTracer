namespace Harborer.Core.Har;

/// <summary>
/// Interns the short strings that repeat across entries (header names, common header values, methods, MIME types),
/// which keeps the index small for 200,000-entry files. Lookups by span avoid allocating duplicates.
/// </summary>
internal sealed class StringPool
{
    private const int MaxEntries = 2_000_000;
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public StringPool()
    {
        _lookup = _set.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public string Get(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return "";
        }

        if (_lookup.TryGetValue(chars, out var existing))
        {
            return existing;
        }

        var created = new string(chars);
        if (_set.Count < MaxEntries)
        {
            _set.Add(created);
        }

        return created;
    }
}
