using System.Security.Cryptography.X509Certificates;
using Harborer.Core.Http;

namespace Harborer.Net.Tests;

public sealed class TimingAndTlsTests
{
    private static readonly LoopbackServerOptions s_tls = new() { Certificate = TestCertificates.Server };

    [Fact]
    public async Task HttpTimingsAreMeasuredAndDnsIsNotApplicableForAnIpLiteral()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text(new string('x', 5000)));

        var outcome = await Send.RunAsync(Send.Get(server.Url("/t")));

        Assert.True(outcome.Succeeded, outcome.Error);
        var timings = Assert.Single(outcome.Exchanges).Timings;
        Assert.Equal(-1, timings.Dns);
        Assert.Equal(-1, timings.Ssl);
        Assert.True(timings.Blocked >= 0, $"blocked {timings.Blocked}");
        Assert.True(timings.Connect >= 0, $"connect {timings.Connect}");
        Assert.True(timings.Send >= 0, $"send {timings.Send}");
        Assert.True(timings.Wait >= 0, $"wait {timings.Wait}");
        Assert.True(timings.Receive >= 0, $"receive {timings.Receive}");
        Assert.True(timings.Total >= timings.Connect + timings.Send + timings.Wait, $"total {timings.Total}");
    }

    [Fact]
    public async Task HttpsTimingsIncludeSslInsideConnect()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("tls"), s_tls);
        var spec = Send.Get(server.Url("/t"));
        spec.Options.Insecure = true;

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var timings = Assert.Single(outcome.Exchanges).Timings;
        Assert.Equal(-1, timings.Dns);
        Assert.True(timings.Ssl >= 0, $"ssl {timings.Ssl}");
        Assert.True(timings.Connect >= timings.Ssl, $"connect {timings.Connect} ssl {timings.Ssl}");
        Assert.True(
            timings.Send >= 0 && timings.Wait >= 0 && timings.Receive >= 0,
            $"send {timings.Send} wait {timings.Wait} receive {timings.Receive}");
    }

    [Fact]
    public void WriteCompletingAfterTheFirstReadStillEndsTheSendPhase()
    {
        // A read can be pending while the request is written; the response may land before the write continuation.
        var recorder = new WireRecorder(1024);

        bool beforeFirstRead = recorder.OnWriteStarting("GET / HTTP/1.1\r\n\r\n"u8);
        recorder.OnRead("HTTP/1.1 200 OK\r\n\r\n"u8);
        recorder.OnWriteCompleted(beforeFirstRead);
        bool late = recorder.OnWriteStarting("x"u8);
        recorder.OnWriteCompleted(late);
        var snapshot = recorder.Snapshot();

        Assert.True(beforeFirstRead);
        Assert.False(late);
        Assert.NotEqual(0, snapshot.LastWriteEndBeforeFirstRead);
        Assert.True(snapshot.LastWriteEndBeforeFirstRead <= snapshot.LastWriteEnd);
        Assert.Equal("GET / HTTP/1.1\r\n\r\nx", Send.Text(snapshot.Sent));
    }

    [Fact]
    public async Task DnsIsTimedForAHostName()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("named"));

        var outcome = await Send.RunAsync(Send.Get($"http://localhost:{server.Port}/"));

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.True(exchange.Timings.Dns >= 0, $"dns {exchange.Timings.Dns}");
        Assert.Equal("127.0.0.1", exchange.RemoteAddress);
    }

    [Fact]
    public async Task TlsDetailsAreCaptured()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("tls"), s_tls);
        var spec = Send.Get($"https://localhost:{server.Port}/");
        spec.Options.Insecure = true;

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        var tls = Assert.IsType<Harborer.Core.Engine.TlsDetails>(exchange.Tls);
        Assert.StartsWith("TLS 1.", tls.Protocol, StringComparison.Ordinal);
        Assert.StartsWith("TLS_", tls.CipherSuite, StringComparison.Ordinal);
        Assert.Equal("http/1.1", tls.ApplicationProtocol);
        Assert.Equal("CN=example.test", tls.Subject);
        Assert.Equal("CN=example.test", tls.Issuer);
        Assert.Equal(TestCertificates.Server.Thumbprint, tls.Thumbprint);
        Assert.True(tls.NotBefore < DateTimeOffset.Now && tls.NotAfter > DateTimeOffset.Now);
        Assert.Equal(["DNS:example.test", "DNS:localhost", "IP:127.0.0.1"], tls.SubjectAlternativeNames);
        Assert.Equal("localhost", tls.ServerName);
        Assert.StartsWith("RemoteCertificateChainErrors", tls.PolicyErrors, StringComparison.Ordinal);
        Assert.Equal("HTTP/1.1", exchange.HttpVersion);
        Assert.Equal("localhost", Assert.Single(server.Requests).Sni);
    }

    [Fact]
    public async Task UntrustedCertificateWithoutInsecureFailsWithACurlStyleError()
    {
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("never"), s_tls);

        var outcome = await Send.RunAsync(Send.Get(server.Url("/")));

        Assert.False(outcome.Succeeded);
        Assert.Equal("SSL certificate problem: self-signed certificate", outcome.Error);
        var exchange = Assert.Single(outcome.Exchanges);
        Assert.Equal(0, exchange.Status);
        Assert.Equal(outcome.Error, exchange.Error);
        Assert.Equal("CN=example.test", exchange.Tls?.Subject);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void NameMismatchIsReportedWhenTheChainIsNotTheProblem()
    {
        // With the chain trusted the only remaining error is the name: exercise the message builder directly.
        string message = CertificateInspector.FailureMessage(
            System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, null, TestCertificates.Server, "wrong.test");

        Assert.Equal("SSL: no alternative certificate subject name matches target host name 'wrong.test'", message);
    }

    [Fact]
    public async Task ClientCertificateFromPfxIsPresented()
    {
        using var temp = new TempDirectory();
        string pfx = TestCertificates.WriteClientPfx(temp.Path, "pa55");
        await using var server = LoopbackServer.Start(
            ScriptedResponse.Text("hello client"),
            new LoopbackServerOptions { Certificate = TestCertificates.Server, RequireClientCertificate = true });
        var spec = Send.Get(server.Url("/mtls"));
        spec.Options.Insecure = true;
        spec.Options.ClientCertificate = new ClientCertificateSpec
        {
            Source = ClientCertificateSource.PfxFile,
            Path = "client.pfx",
            Password = "pa55",
        };

        var outcome = await Send.RunAsync(spec, new Harborer.Core.Engine.SendSettings { FileBaseDirectory = temp.Path });

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(TestCertificates.Client.Thumbprint, Assert.Single(server.Requests).ClientCertificateThumbprint);
        Assert.True(File.Exists(pfx));
    }

    [Fact]
    public async Task ClientCertificateWithWrongPasswordFailsBeforeConnecting()
    {
        using var temp = new TempDirectory();
        string pfx = TestCertificates.WriteClientPfx(temp.Path, "right");
        await using var server = LoopbackServer.Start(ScriptedResponse.Text("never"), s_tls);
        var spec = Send.Get(server.Url("/"));
        spec.Options.ClientCertificate = new ClientCertificateSpec { Path = pfx, Password = "wrong" };

        var outcome = await Send.RunAsync(spec);

        Assert.StartsWith("Could not load the client certificate", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(outcome.Exchanges);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task PemClientCertificateLoads()
    {
        using var temp = new TempDirectory();
        var client = TestCertificates.Client;
        string certPath = temp.Write("client.crt", client.ExportCertificatePem());
        string keyPath = temp.Write("client.key", client.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        await using var server = LoopbackServer.Start(
            ScriptedResponse.Text("pem"),
            new LoopbackServerOptions { Certificate = TestCertificates.Server, RequireClientCertificate = true });
        var spec = Send.Get(server.Url("/"));
        spec.Options.Insecure = true;
        spec.Options.ClientCertificate = new ClientCertificateSpec
        {
            Source = ClientCertificateSource.PemFile,
            Path = certPath,
            KeyPath = keyPath,
        };

        var outcome = await Send.RunAsync(spec);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(client.Thumbprint, Assert.Single(server.Requests).ClientCertificateThumbprint);
    }
}
