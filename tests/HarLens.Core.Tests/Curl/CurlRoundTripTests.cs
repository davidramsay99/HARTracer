using HarLens.Core.Curl;
using HarLens.Core.Http;

namespace HarLens.Core.Tests.Curl;

/// <summary>Parse(generate(request)) is equivalent to request for every curl format.</summary>
public class CurlRoundTripTests
{
    public static readonly ExportFormat[] CurlFormats = [ExportFormat.CurlBash, ExportFormat.CurlCmd, ExportFormat.CurlPowerShell];

    public static TheoryData<string, ExportFormat> FixtureCases()
    {
        var data = new TheoryData<string, ExportFormat>();
        foreach (var name in CurlFixtures.Names())
        {
            foreach (var format in CurlFormats)
            {
                data.Add(name, format);
            }
        }

        return data;
    }

    public static TheoryData<string, ExportFormat> CorpusCases()
    {
        var data = new TheoryData<string, ExportFormat>();
        foreach (var (name, _) in AdversarialCorpus.Requests())
        {
            foreach (var format in CurlFormats)
            {
                data.Add(name, format);
            }
        }

        return data;
    }

    private static CurlDialect DialectOf(ExportFormat format) => format switch
    {
        ExportFormat.CurlCmd => CurlDialect.Cmd,
        ExportFormat.CurlPowerShell => CurlDialect.PowerShell,
        _ => CurlDialect.Bash,
    };

    internal static void AssertRoundTrip(HttpRequestSpec request, ExportFormat format)
    {
        var export = RequestExporter.Export(request, format);
        Assert.DoesNotContain(export.Warnings, w => w.Contains("does not reproduce", StringComparison.Ordinal));

        // Auto-detection must pick the right dialect for our own output.
        var parsed = CurlParser.Parse(export.Text);
        Assert.Equal(DialectOf(format), parsed.Dialect);
        var differences = RequestEquivalence.Differences(parsed.Request, request);
        Assert.True(differences.Count == 0, $"{format} output:\n{export.Text}\nDifferences:\n{string.Join("\n", differences)}");

        // Windows clipboards turn LF into CRLF; the command must survive that too.
        var crlf = CurlParser.Parse(export.Text.Replace("\n", "\r\n", StringComparison.Ordinal));
        var crlfDifferences = RequestEquivalence.Differences(crlf.Request, request);
        Assert.True(crlfDifferences.Count == 0, $"{format} output with CRLF:\nDifferences:\n{string.Join("\n", crlfDifferences)}");
    }

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void FixtureRequestsRoundTrip(string name, ExportFormat format) =>
        AssertRoundTrip(CurlFixtures.Expectation(name).Request, format);

    [Theory]
    [MemberData(nameof(CorpusCases))]
    public void AdversarialRequestsRoundTrip(string name, ExportFormat format) =>
        AssertRoundTrip(AdversarialCorpus.Requests().Single(r => r.Name == name).Request, format);

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void ImportedFixturesExportAndReimportUnchanged(string name, ExportFormat format)
    {
        // The full loop a user performs: paste a browser command, then copy it out again in another dialect.
        var imported = CurlParser.Parse(CurlFixtures.Command(name)).Request;
        AssertRoundTrip(imported, format);
    }

    [Theory]
    [InlineData(ExportFormat.CurlBash)]
    [InlineData(ExportFormat.CurlCmd)]
    [InlineData(ExportFormat.CurlPowerShell)]
    public void PseudoHeadersAndContentLengthAreOmittedByDefault(ExportFormat format)
    {
        var request = new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://example.com/x",
            Headers =
            [
                new HeaderEntry(":authority", "example.com"),
                new HeaderEntry(":method", "POST"),
                new HeaderEntry(":path", "/x"),
                new HeaderEntry(":scheme", "https"),
                new HeaderEntry("content-type", "text/plain"),
                new HeaderEntry("content-length", "3"),
                new HeaderEntry("x-off", "1", enabled: false),
            ],
            Body = RequestBody.FromText("abc"),
        };

        var export = RequestExporter.Export(request, format);
        Assert.DoesNotContain(":authority", export.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("content-length", export.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-off", export.Text, StringComparison.Ordinal);
        Assert.Empty(export.Warnings);

        var parsed = CurlParser.Parse(export.Text).Request;
        Assert.Equal(["content-type"], parsed.Headers.Select(h => h.Name));
        Assert.Equal("abc", parsed.Body.Text);
    }

    [Fact]
    public void IncludeOptionsExportPseudoHeadersAndContentLength()
    {
        var request = new HttpRequestSpec
        {
            Url = "https://example.com/x",
            Headers = [new HeaderEntry(":authority", "example.com"), new HeaderEntry("Content-Length", "0")],
        };

        var export = RequestExporter.Export(request, ExportFormat.CurlBash, new ExportOptions { IncludeContentLength = true, IncludePseudoHeaders = true });
        Assert.Contains("-H ':authority: example.com'", export.Text, StringComparison.Ordinal);
        Assert.Contains("-H 'Content-Length: 0'", export.Text, StringComparison.Ordinal);
        Assert.Contains(export.Warnings, w => w.Contains("pseudo-header", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ExportFormat.CurlBash)]
    [InlineData(ExportFormat.CurlCmd)]
    [InlineData(ExportFormat.CurlPowerShell)]
    public void NonTextBodyIsExportedAsPlaceholderFileWithWarning(ExportFormat format)
    {
        var request = new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://example.com/bin",
            Body = new RequestBody { Mode = BodyMode.Raw, Bytes = [0x00, 0xFF, 0x10, 0x80] },
        };

        var export = RequestExporter.Export(request, format);
        Assert.Contains("@request-body.bin", export.Text, StringComparison.Ordinal);
        Assert.Contains(export.Warnings, w => w.Contains("request-body.bin", StringComparison.Ordinal));
        var parsed = CurlParser.Parse(export.Text).Request;
        Assert.Equal(BodyMode.BinaryFile, parsed.Body.Mode);
        Assert.Equal("request-body.bin", parsed.Body.FilePath);
    }

    [Fact]
    public void BlankHeaderValueIsReportedAsNotRepresentable()
    {
        var request = new HttpRequestSpec { Url = "https://example.com/", Headers = [new HeaderEntry("X-Blank", "   ")] };
        var export = RequestExporter.Export(request, ExportFormat.CurlBash);
        Assert.Contains("-H 'X-Blank;'", export.Text, StringComparison.Ordinal);
        Assert.Contains(export.Warnings, w => w.Contains("blank value", StringComparison.Ordinal));
    }

    [Fact]
    public void SelfCheckReportsAnythingTheParserReadsDifferently()
    {
        // A proxy without a scheme reads back with http:// added, as curl does; the export says so.
        var request = new HttpRequestSpec { Url = "https://example.com/", Options = { Proxy = "proxy.local:3128" } };
        var export = RequestExporter.Export(request, ExportFormat.CurlBash);
        Assert.Contains(export.Warnings, w => w.Contains("does not reproduce", StringComparison.Ordinal) && w.Contains("proxy", StringComparison.Ordinal));
    }
}
