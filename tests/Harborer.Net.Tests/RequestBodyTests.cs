using System.Text;
using Harborer.Core.Engine;
using Harborer.Core.Http;

namespace Harborer.Net.Tests;

public sealed class RequestBodyTests
{
    private static HttpRequestSpec Post(string url, RequestBody body, params (string Name, string Value)[] headers)
    {
        var spec = Send.Get(url, headers);
        spec.Method = "POST";
        spec.Body = body;
        return spec;
    }

    [Fact]
    public async Task MultipartBodyHasTextAndFilePartsAndTheBoundaryInTheHeader()
    {
        using var temp = new TempDirectory();
        temp.Write("notes.txt", "file content\n");
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var body = new RequestBody
        {
            Mode = BodyMode.Multipart,
            Parts =
            [
                new MultipartPart { Name = "field", Value = "value" },
                new MultipartPart { Name = "upload", FilePath = "notes.txt" },
                new MultipartPart { Name = "skipped", Value = "no", Enabled = false },
                new MultipartPart { Name = "typed", FilePath = "notes.txt", FileName = "renamed.bin", ContentType = "application/x-custom" },
                new MultipartPart { Name = "inline", FilePath = "notes.txt", FileContentAsValue = true },
            ],
        };

        var outcome = await Send.RunAsync(Post(server.Url("/upload"), body), new SendSettings { FileBaseDirectory = temp.Path });

        Assert.True(outcome.Succeeded, outcome.Error);
        var received = Assert.Single(server.Requests);
        string contentType = received.Header("Content-Type")!;
        Assert.StartsWith("multipart/form-data; boundary=------------------------", contentType, StringComparison.Ordinal);
        string boundary = contentType["multipart/form-data; boundary=".Length..];
        string expected =
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\nvalue\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"upload\"; filename=\"notes.txt\"\r\nContent-Type: text/plain\r\n\r\nfile content\n\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"typed\"; filename=\"renamed.bin\"\r\nContent-Type: application/x-custom\r\n\r\nfile content\n\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"inline\"\r\n\r\nfile content\n\r\n" +
            $"--{boundary}--\r\n";
        Assert.Equal(expected, received.BodyText);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), outcome.Exchanges[0].RequestBody);
    }

    [Fact]
    public async Task ComposedMultipartBoundaryIsKept()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var body = new RequestBody { Mode = BodyMode.Multipart, Parts = [new MultipartPart { Name = "a", Value = "1" }] };

        var outcome = await Send.RunAsync(Post(server.Url("/"), body, ("Content-Type", "multipart/form-data; boundary=\"my-boundary\"")));

        Assert.True(outcome.Succeeded, outcome.Error);
        var received = Assert.Single(server.Requests);
        Assert.Equal("multipart/form-data; boundary=\"my-boundary\"", received.Header("Content-Type"));
        Assert.StartsWith("--my-boundary\r\n", received.BodyText, StringComparison.Ordinal);
        Assert.EndsWith("--my-boundary--\r\n", received.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposedMultipartTypeWithoutBoundaryGetsOneAppended()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var body = new RequestBody { Mode = BodyMode.Multipart, Parts = [new MultipartPart { Name = "a", Value = "1" }] };

        var outcome = await Send.RunAsync(Post(server.Url("/"), body, ("Content-Type", "multipart/mixed")));

        Assert.True(outcome.Succeeded, outcome.Error);
        string contentType = Assert.Single(server.Requests).Header("Content-Type")!;
        Assert.StartsWith("multipart/mixed; boundary=", contentType, StringComparison.Ordinal);
        Assert.True(MultipartBuilder.TryGetBoundary(contentType, out var boundary));
        Assert.StartsWith("--" + boundary, Assert.Single(server.Requests).BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BinaryFileBodyIsSentByteForByte()
    {
        using var temp = new TempDirectory();
        byte[] content = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        temp.Write("blob.bin", content);
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var body = new RequestBody { Mode = BodyMode.BinaryFile, FilePath = Path.Combine("..", Path.GetFileName(temp.Path), "blob.bin") };

        var outcome = await Send.RunAsync(
            Post(server.Url("/bin"), body, ("Content-Type", "application/octet-stream")),
            new SendSettings { FileBaseDirectory = temp.Path });

        Assert.True(outcome.Succeeded, outcome.Error);
        var received = Assert.Single(server.Requests);
        Assert.Equal(content, received.Body);
        Assert.Equal("256", received.Header("Content-Length"));
    }

    [Fact]
    public async Task MissingFileFailsBeforeAnyConnection()
    {
        using var temp = new TempDirectory();
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("never"));
        var body = new RequestBody { Mode = BodyMode.Multipart, Parts = [new MultipartPart { Name = "f", FilePath = "absent.txt" }] };

        var outcome = await Send.RunAsync(Post(server.Url("/"), body), new SendSettings { FileBaseDirectory = temp.Path });

        Assert.Equal($"File not found for multipart part 'f': {Path.Combine(temp.Path, "absent.txt")}", outcome.Error);
        Assert.Empty(outcome.Exchanges);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task RelativePathWithoutBaseDirectoryIsAnError()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("never"));
        var body = new RequestBody { Mode = BodyMode.BinaryFile, FilePath = "data.bin" };

        var outcome = await Send.RunAsync(Post(server.Url("/"), body));

        Assert.Contains("no base directory", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task JsonBodyGetsAContentTypeOnlyWhenNoneWasComposed()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));

        await Send.RunAsync(Post(server.Url("/a"), RequestBody.FromText("{\"a\":1}", BodyMode.Json)));
        await Send.RunAsync(Post(server.Url("/b"), RequestBody.FromText("{}", BodyMode.Json), ("Content-Type", "application/vnd.api+json")));

        var requests = server.Requests.ToArray();
        Assert.Equal("application/json", requests[0].Header("Content-Type"));
        Assert.Equal("{\"a\":1}", requests[0].BodyText);
        Assert.Equal("application/vnd.api+json", requests[1].Header("Content-Type"));
    }

    [Fact]
    public async Task RawBytesTakePrecedenceOverText()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));
        var body = new RequestBody { Mode = BodyMode.Raw, Text = "ignored", Bytes = [0xFF, 0x00, 0x7F] };

        await Send.RunAsync(Post(server.Url("/"), body));

        var received = Assert.Single(server.Requests);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x7F }, received.Body);
        Assert.False(received.HasHeader("Content-Type"));
    }

    [Fact]
    public async Task FormBodyIsSentAsComposed()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("ok"));

        await Send.RunAsync(Post(server.Url("/"), RequestBody.FromText("a=1&b=two%20words", BodyMode.FormUrlEncoded)));

        var received = Assert.Single(server.Requests);
        Assert.Equal("a=1&b=two%20words", received.BodyText);
        Assert.Equal("application/x-www-form-urlencoded", received.Header("Content-Type"));
    }

    [Fact]
    public void BoundaryParsingHandlesQuotesAndCase()
    {
        Assert.True(MultipartBuilder.TryGetBoundary("multipart/form-data; BOUNDARY=\"a b\"", out var quoted));
        Assert.Equal("a b", quoted);
        Assert.True(MultipartBuilder.TryGetBoundary("multipart/form-data;charset=x;boundary=xyz", out var plain));
        Assert.Equal("xyz", plain);
        Assert.False(MultipartBuilder.TryGetBoundary("multipart/form-data", out _));
    }
}

public sealed class LoaderTests
{
    [Fact]
    public void EngineIsCreatedThroughTheReflectionLoader()
    {
        var engine = RequestEngineLoader.Load();

        Assert.IsType<RequestEngine>(engine);
        Assert.True(RequestEngineLoader.IsNetworkAssemblyLoaded);
    }
}
