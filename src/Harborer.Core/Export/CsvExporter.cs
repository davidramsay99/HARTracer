using System.Globalization;
using System.Text;
using Harborer.Core.Har;
using Harborer.Core.Http;

namespace Harborer.Core.Export;

/// <summary>A list column for CSV export: header text and a value selector.</summary>
public sealed record CsvColumn(string Header, Func<HarEntry, string> Value, bool Numeric = false);

/// <summary>Exports the session list as CSV, RFC 4180 quoting, UTF-8 with BOM for Excel.</summary>
public static class CsvExporter
{
    public static IReadOnlyList<CsvColumn> DefaultColumns(DateTimeOffset firstStart) =>
    [
        new("#", e => e.Id.ToString(CultureInfo.InvariantCulture), Numeric: true),
        new("Status", e => e.Status.ToString(CultureInfo.InvariantCulture), Numeric: true),
        new("Method", e => e.Method),
        new("Protocol", e => RawMessage.Http1Version(string.IsNullOrEmpty(e.ResponseHttpVersion) ? e.RequestHttpVersion : e.ResponseHttpVersion)),
        new("Host", e => e.HostDisplay),
        new("Path", e => e.Path),
        new("MIME type", e => e.MimeTypeBase),
        new("Response size", e => e.ResponseSize.ToString(CultureInfo.InvariantCulture), Numeric: true),
        new("Total time (ms)", e => e.TotalTime.ToString("0.###", CultureInfo.InvariantCulture), Numeric: true),
        new("Started (ms)", e => e.StartedDateTime == DateTimeOffset.MinValue ? "" : (e.StartedDateTime - firstStart).TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture), Numeric: true),
        new("Started (UTC)", e => e.StartedDateTime == DateTimeOffset.MinValue ? "" : e.StartedDateTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
        new("URL", e => e.Url),
        new("Remote address", e => e.ServerIPAddress ?? ""),
        new("Error", e => e.Error ?? ""),
        new("Comment", e => e.Comment ?? ""),
        new("Original index", e => e.FileIndex.ToString(CultureInfo.InvariantCulture), Numeric: true),
    ];

    public static void Write(TextWriter writer, IEnumerable<HarEntry> entries, IReadOnlyList<CsvColumn> columns)
    {
        writer.Write(string.Join(",", columns.Select(c => Quote(c.Header, numeric: false))));
        writer.Write("\r\n");
        foreach (var entry in entries)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                {
                    writer.Write(',');
                }

                writer.Write(Quote(columns[i].Value(entry), columns[i].Numeric));
            }

            writer.Write("\r\n");
        }
    }

    public static void Save(string path, IEnumerable<HarEntry> entries, IReadOnlyList<CsvColumn> columns)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Write(writer, entries, columns);
    }

    /// <summary>
    /// Quotes when needed. Text cells that a spreadsheet would read as a formula (leading = + - @ tab CR) get a
    /// leading apostrophe, since header values in a customer HAR are untrusted.
    /// </summary>
    public static string Quote(string? value, bool numeric)
    {
        value ??= "";
        if (!numeric && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        return sb.ToString();
    }
}
