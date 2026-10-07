namespace HarLens.Core.Tests.Fixtures;

/// <summary>
/// A test that generates hundreds of megabytes of fixture data. Runs when HARLENS_LARGE_TESTS=1
/// (the CI performance job); skipped otherwise to keep the default run fast.
/// </summary>
public sealed class LargeFactAttribute : FactAttribute
{
    public LargeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HARLENS_LARGE_TESTS") != "1")
        {
            Skip = "Set HARLENS_LARGE_TESTS=1 to run large-file tests.";
        }
    }
}
