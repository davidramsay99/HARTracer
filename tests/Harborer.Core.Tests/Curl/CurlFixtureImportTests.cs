using Harborer.Core.Curl;
using Harborer.Core.Http;

namespace Harborer.Core.Tests.Curl;

/// <summary>Each browser-style fixture imports to its expected request model.</summary>
public class CurlFixtureImportTests
{
    public static TheoryData<string> Fixtures() => CurlFixtures.All();

    [Fact]
    public void FixtureSetCoversEveryBrowserDialect()
    {
        var names = CurlFixtures.Names().ToList();
        Assert.Contains(names, n => n.StartsWith("chrome-bash-", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("chrome-cmd-", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("firefox-", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith("powershell-", StringComparison.Ordinal));
        Assert.True(names.Count >= 12);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ImportsToExpectedModel(string name)
    {
        var expected = CurlFixtures.Expectation(name);
        var result = CurlParser.Parse(CurlFixtures.Command(name));
        AssertMatches(expected, result);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ImportsTheSameWithCrlfLineEndings(string name)
    {
        var expected = CurlFixtures.Expectation(name);
        var text = CurlFixtures.Command(name).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        var result = CurlParser.Parse("  \r\n" + text + "\r\n\r\n  ");
        AssertMatches(expected, result);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ImportsTheSameWithExplicitDialect(string name)
    {
        var expected = CurlFixtures.Expectation(name);
        var result = CurlParser.Parse(CurlFixtures.Command(name), new CurlParseOptions { Dialect = expected.Dialect });
        AssertMatches(expected, result);
    }

    internal static void AssertMatches(CurlFixtureExpectation expected, CurlImportResult result)
    {
        Assert.Equal(expected.Dialect, result.Dialect);
        var differences = RequestEquivalence.Differences(result.Request, expected.Request);
        Assert.True(differences.Count == 0, "Differences:\n" + string.Join("\n", differences));
        Assert.All(result.Request.Headers, h => Assert.True(h.Enabled));
        if (expected.BodyMode is { } mode)
        {
            Assert.Equal(mode, result.Request.Body.Mode);
        }

        var expectedWarnings = expected.Warnings ?? [];
        Assert.True(expectedWarnings.Count == result.Warnings.Count, "Warnings:\n" + string.Join("\n", result.Warnings));
        foreach (var fragment in expectedWarnings)
        {
            Assert.True(result.Warnings.Any(w => w.Contains(fragment, StringComparison.Ordinal)), $"No warning contains '{fragment}'. Warnings:\n" + string.Join("\n", result.Warnings));
        }

        Assert.Equal(expected.IgnoredOptions ?? [], result.IgnoredOptions);
    }
}
