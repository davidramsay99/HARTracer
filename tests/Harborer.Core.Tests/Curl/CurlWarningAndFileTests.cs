using System.Text;
using Harborer.Core.Curl;
using Harborer.Core.Http;

namespace Harborer.Core.Tests.Curl;

public sealed class CurlWarningAndFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "harborer-curl-" + Guid.NewGuid().ToString("N"));

    public CurlWarningAndFileTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllBytes(Path.Combine(_dir, "form.txt"), Encoding.UTF8.GetBytes("a=1\r\n&b=2\n"));
        File.WriteAllBytes(Path.Combine(_dir, "sub", "note.txt"), Encoding.UTF8.GetBytes("héllo wörld\n"));
        File.WriteAllBytes(Path.Combine(_dir, "headers.txt"), Encoding.UTF8.GetBytes("X-One: 1\r\n\r\nX-Two: 2\n"));
        File.WriteAllBytes(Path.Combine(_dir, "photo.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(_dir, "body.json"), Encoding.UTF8.GetBytes("{\"k\":\"v\"}\n"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CurlImportResult ParseWithBase(string command) =>
        CurlParser.Parse(command, new CurlParseOptions { FileBaseDirectory = _dir });

    // ---- warnings for options ----

    [Fact]
    public void UnknownOptionIsNamedInAWarningAndImportCompletes()
    {
        var result = CurlParser.Parse("curl --frobnicate -H 'A: 1' https://x/");
        Assert.Equal("https://x/", result.Request.Url);
        Assert.Equal("1", result.Request.GetHeader("A"));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("--frobnicate", warning, StringComparison.Ordinal);
        Assert.Equal(["--frobnicate"], result.IgnoredOptions);
    }

    [Fact]
    public void OptionEqualsValueFormGetsAHint()
    {
        var result = CurlParser.Parse("curl --request=POST https://x/");
        Assert.Equal("GET", result.Request.Method);
        Assert.Contains("'--request POST'", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellBareAtSignWarns()
    {
        var result = CurlParser.Parse("curl.exe -d @body.json 'https://x/'");
        Assert.Equal(CurlDialect.PowerShell, result.Dialect);
        Assert.Contains(result.Warnings, w => w.Contains("splatting", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownShortOptionIsNamed()
    {
        var result = CurlParser.Parse("curl -W https://x/");
        Assert.Contains("'-W'", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--cacert ca.pem")]
    [InlineData("-c jar.txt")]
    [InlineData("--cookie-jar jar.txt")]
    [InlineData("-D headers.txt")]
    [InlineData("-T upload.bin")]
    [InlineData("-r 0-99")]
    [InlineData("--retry 3")]
    [InlineData("--limit-rate 1M")]
    [InlineData("--interface eth0")]
    [InlineData("-U user:pw")]
    [InlineData("--noproxy '*'")]
    [InlineData("--aws-sigv4 aws:amz:us-east-1:s3")]
    [InlineData("-K config.txt")]
    [InlineData("--trace-ascii out.txt")]
    [InlineData("-z 'Wed, 01 Jan 2025 00:00:00 GMT'")]
    [InlineData("--unix-socket /run/x.sock")]
    [InlineData("--doh-url https://doh.example/dns-query")]
    [InlineData("--proto =https")]
    [InlineData("--tls-max 1.2")]
    [InlineData("--expand-cacert ca.pem")]
    public void UnsupportedOptionArgumentIsSkippedNotTakenForTheUrl(string option)
    {
        var result = CurlParser.Parse($"curl {option} https://x/");
        Assert.Equal("https://x/", result.Request.Url);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(option.Split(' ')[0], warning, StringComparison.Ordinal);
        Assert.Contains("argument", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputOnlyOptionsAreIgnoredSilently()
    {
        var result = CurlParser.Parse("curl -s -v -i -S -o out.json -w '%{http_code}' --silent --verbose --include --show-error --output out2 --write-out x -sSvi https://x/");
        Assert.Empty(result.Warnings);
        Assert.Equal("https://x/", result.Request.Url);
        Assert.Equal(
            ["-s", "-v", "-i", "-S", "-o", "-w", "--silent", "--verbose", "--include", "--show-error", "--output", "--write-out", "-s", "-S", "-v", "-i"],
            result.IgnoredOptions);
    }

    [Fact]
    public void MissingArgumentWarns()
    {
        var result = CurlParser.Parse("curl https://x/ -H");
        Assert.Contains(result.Warnings, w => w.Contains("'-H' needs an argument", StringComparison.Ordinal));
    }

    // ---- @file without a base directory ----

    [Theory]
    [InlineData("-d @form.txt", "-d")]
    [InlineData("--data @form.txt", "--data")]
    [InlineData("--data-ascii @form.txt", "--data-ascii")]
    [InlineData("--data-urlencode name@form.txt", "--data-urlencode")]
    [InlineData("--json @form.txt", "--json")]
    [InlineData("-H @form.txt", "-H")]
    public void FileReadAtImportIsNeverReadWithoutABaseDirectory(string option, string written)
    {
        var result = CurlParser.Parse($"curl https://x/ {option}");
        var reference = Assert.Single(result.FileReferences);
        Assert.Equal((written, "form.txt", null, false, false), (reference.Option, reference.Path, reference.ResolvedPath, reference.Resolved, reference.ReadAtSendTime));
        Assert.Contains(result.Warnings, w => w.Contains("Choose a base directory", StringComparison.Ordinal) && w.Contains("form.txt", StringComparison.Ordinal));
        Assert.DoesNotContain("a=1", result.Request.Body.Text ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void SendTimeFilesKeepTheirPathAndAreReportedWithoutABaseDirectory()
    {
        var result = CurlParser.Parse("curl https://x/ -F 'p=@photo.png' -F 'n=<sub/note.txt'");
        Assert.Equal(["photo.png", "sub/note.txt"], result.Request.Body.Parts.Select(p => p.FilePath));
        Assert.All(result.FileReferences, r => Assert.True(r.ReadAtSendTime && !r.Resolved && r.ResolvedPath is null));
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("Choose a base directory", StringComparison.Ordinal)));
    }

    // ---- @file with a base directory ----

    [Fact]
    public void DataFileIsReadWithLineBreaksStripped()
    {
        var result = ParseWithBase("curl https://x/ -d @form.txt -d c=3");
        Assert.Equal("a=1&b=2&c=3", result.Request.Body.Text);
        var reference = Assert.Single(result.FileReferences);
        Assert.Equal((Path.Combine(_dir, "form.txt"), true, false), (reference.ResolvedPath, reference.Resolved, reference.ReadAtSendTime));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void UrlEncodeAndJsonFilesAreReadAsIs()
    {
        Assert.Equal("n=h%C3%A9llo+w%C3%B6rld%0A", ParseWithBase("curl https://x/ --data-urlencode n@sub/note.txt").Request.Body.Text);
        Assert.Equal("{\"k\":\"v\"}\n", ParseWithBase("curl https://x/ --json @body.json").Request.Body.Text);
    }

    [Fact]
    public void HeaderFileAddsOneHeaderPerLine()
    {
        var request = ParseWithBase("curl https://x/ -H @headers.txt").Request;
        Assert.Equal(["X-One", "X-Two"], request.Headers.Select(h => h.Name));
    }

    [Fact]
    public void SendTimeFilesAreResolvedAgainstTheBaseDirectory()
    {
        var result = ParseWithBase("curl https://x/ -F 'p=@photo.png;type=image/png' --cert sub/note.txt");
        Assert.Equal(Path.Combine(_dir, "photo.png"), result.Request.Body.Parts[0].FilePath);
        Assert.Equal(Path.Combine(_dir, "sub", "note.txt"), result.Request.Options.ClientCertificate!.Path);
        Assert.All(result.FileReferences, r => Assert.True(r.Resolved && r.ReadAtSendTime));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SingleBinaryFileBecomesABinaryFileBody()
    {
        var request = ParseWithBase("curl https://x/ --data-binary @photo.png").Request;
        Assert.Equal((BodyMode.BinaryFile, Path.Combine(_dir, "photo.png")), (request.Body.Mode, request.Body.FilePath));
        Assert.Equal("application/x-www-form-urlencoded", request.GetHeader("Content-Type"));
    }

    [Fact]
    public void BinaryFileMixedWithOtherDataIsReadNow()
    {
        var request = ParseWithBase("curl https://x/ --data-binary @sub/note.txt -d z=1").Request;
        Assert.Equal("héllo wörld\n&z=1", request.Body.Text);
    }

    [Fact]
    public void AbsolutePathsAreUsedAsGiven()
    {
        var absolute = Path.Combine(_dir, "form.txt");
        var result = CurlParser.Parse($"curl https://x/ -d '@{absolute}'", new CurlParseOptions { FileBaseDirectory = Path.Combine(_dir, "sub") });
        Assert.Equal("a=1&b=2", result.Request.Body.Text);
    }

    [Fact]
    public void MissingFileWarnsAndLeavesContentEmpty()
    {
        var result = ParseWithBase("curl https://x/ -d @nope.txt -F 'f=@gone.bin'");
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("was not found", StringComparison.Ordinal)));
        Assert.All(result.FileReferences, r => Assert.False(r.Resolved));
    }

    [Fact]
    public void StandardInputIsNotAvailable()
    {
        var result = ParseWithBase("curl https://x/ -d @- --data-binary @-");
        Assert.Equal("&", result.Request.Body.Text);
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("standard input", StringComparison.Ordinal)));
    }

    [Fact]
    public void VariableFromFile()
    {
        var request = ParseWithBase("curl --variable n@sub/note.txt --expand-data '{{n:trim}}' https://x/").Request;
        Assert.Equal("héllo wörld", request.Body.Text);
    }
}
