using System.Net;
using System.Net.Security;
using HarLens.Core.Engine;
using HarLens.Core.Http;

namespace HarLens.Net;

/// <summary>State shared by every hop of one Send: options, the explicit proxy, the client certificate and the clocks.</summary>
internal sealed class SendContext
{
    private readonly CancellationTokenSource _timeoutSource;

    public SendContext(
        RequestOptions options,
        SendSettings settings,
        WebProxy? proxy,
        SslStreamCertificateContext? clientCertificate,
        CancellationToken userToken,
        CancellationTokenSource timeoutSource,
        long startTimestamp)
    {
        Options = options;
        Settings = settings;
        Proxy = proxy;
        ClientCertificate = clientCertificate;
        UserToken = userToken;
        _timeoutSource = timeoutSource;
        StartTimestamp = startTimestamp;
    }

    public RequestOptions Options { get; }

    public SendSettings Settings { get; }

    public WebProxy? Proxy { get; }

    public SslStreamCertificateContext? ClientCertificate { get; }

    /// <summary>The caller's token: cancelling it is the Cancel button.</summary>
    public CancellationToken UserToken { get; }

    /// <summary>When the whole Send started, for the curl -m style timeout message.</summary>
    public long StartTimestamp { get; }

    public bool TimedOut => _timeoutSource.IsCancellationRequested;

    public long ResponseSizeCap
    {
        get
        {
            long cap = Settings.ResponseSizeCap;
            return cap <= 0 || cap > Array.MaxLength ? Array.MaxLength : cap;
        }
    }
}
