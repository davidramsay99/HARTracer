using System.Diagnostics;

namespace HarLens.Isolation.Tests;

/// <summary>
/// Acceptance test 3: the build fails if a banned network API is added to HarLens.Core or HarLens.App.
/// Copies the sources to a temporary directory, adds one offending file, and runs <c>dotnet build</c>.
/// Restore uses the local package cache populated by the normal build, so no network is needed.
/// </summary>
public sealed class BannedApiBuildTests
{
    private const string Offender = """
        namespace HarLens.Offender;

        internal static class UsesNetwork
        {
            public static object Make() => new System.Net.Http.HttpClient();

            public static object Resolve() => System.Net.Dns.GetHostAddresses("example.test");
        }
        """;

    [Theory]
    [InlineData("HarLens.Core")]
    [InlineData("HarLens.App")]
    public void Adding_a_banned_network_api_fails_the_build(string project)
    {
        var repo = FindRepositoryRoot();
        var work = Path.Combine(Path.GetTempPath(), "harlens-banned-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in new[] { "global.json", "Directory.Build.props", "Directory.Packages.props", "NuGet.config" })
            {
                File.Copy(Path.Combine(repo, file), Path.Combine(work, file).EnsureParent());
            }

            CopyDirectory(Path.Combine(repo, "src"), Path.Combine(work, "src"));
            File.WriteAllText(Path.Combine(work, "src", project, "UsesNetwork.cs"), Offender);

            var (exitCode, log) = Run(Path.Combine(work, "src", project, project + ".csproj"));
            Assert.NotEqual(0, exitCode);
            Assert.Contains("RS0030", log, StringComparison.Ordinal);
            Assert.Contains("UsesNetwork.cs", log, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    [Fact]
    public void Unmodified_core_builds_cleanly()
    {
        var repo = FindRepositoryRoot();
        var work = Path.Combine(Path.GetTempPath(), "harlens-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in new[] { "global.json", "Directory.Build.props", "Directory.Packages.props", "NuGet.config" })
            {
                File.Copy(Path.Combine(repo, file), Path.Combine(work, file).EnsureParent());
            }

            CopyDirectory(Path.Combine(repo, "src", "HarLens.Core"), Path.Combine(work, "src", "HarLens.Core"));
            var (exitCode, log) = Run(Path.Combine(work, "src", "HarLens.Core", "HarLens.Core.csproj"));
            Assert.True(exitCode == 0, log);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private static (int ExitCode, string Log) Run(string projectPath)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        // No node reuse and no shared compiler server: lingering build servers would inherit the output pipes
        // and keep this test waiting long after the build has finished.
        var psi = new ProcessStartInfo(dotnet, ["build", projectPath, "-c", "Release", "-nologo", "-v:q", "-nodeReuse:false",
            "-p:UseSharedCompilation=false", "-p:ContinuousIntegrationBuild=false"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet build did not finish in five minutes.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HarLens.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("HarLens.sln not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj"))
            {
                continue;
            }

            File.Copy(file, Path.Combine(to, relative).EnsureParent());
        }
    }
}

internal static class PathExtensions
{
    public static string EnsureParent(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
