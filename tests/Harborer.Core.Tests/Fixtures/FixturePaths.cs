namespace Harborer.Core.Tests.Fixtures;

internal static class FixturePaths
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string Har(string name) => Path.Combine(Root, "har", name);

    /// <summary>Directory for fixtures generated at test time (large files). Never committed.</summary>
    public static string Generated
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "harborer-tests");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static IEnumerable<string> AllHarFixtures() =>
        Directory.EnumerateFiles(Path.Combine(Root, "har"), "*.har*")
            .Where(p => !p.Contains("truncated", StringComparison.Ordinal) && !p.Contains("malformed", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);
}
