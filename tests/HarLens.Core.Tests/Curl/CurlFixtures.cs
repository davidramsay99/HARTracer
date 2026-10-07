using System.Text.Json;
using System.Text.Json.Serialization;
using HarLens.Core.Curl;
using HarLens.Core.Http;

namespace HarLens.Core.Tests.Curl;

/// <summary>Expected outcome of importing one fixture command, stored beside it as NAME.expected.json.</summary>
public sealed class CurlFixtureExpectation
{
    public string? Description { get; set; }

    public CurlDialect Dialect { get; set; }

    public BodyMode? BodyMode { get; set; }

    public HttpRequestSpec Request { get; set; } = new();

    /// <summary>Substrings of the expected warnings, one per warning. Null means no warnings.</summary>
    public List<string>? Warnings { get; set; }

    /// <summary>Expected <see cref="CurlImportResult.IgnoredOptions"/>. Null means none.</summary>
    public List<string>? IgnoredOptions { get; set; }
}

internal static class CurlFixtures
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "fixtures", "curl");

    public static IEnumerable<string> Names() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.txt").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!;

    public static string Command(string name) => File.ReadAllText(Path.Combine(Directory, name + ".txt"));

    public static CurlFixtureExpectation Expectation(string name) =>
        JsonSerializer.Deserialize<CurlFixtureExpectation>(File.ReadAllText(Path.Combine(Directory, name + ".expected.json")), Json)
        ?? throw new InvalidOperationException($"Empty expectation for {name}.");

    public static TheoryData<string> All()
    {
        var data = new TheoryData<string>();
        foreach (var name in Names())
        {
            data.Add(name);
        }

        return data;
    }
}
