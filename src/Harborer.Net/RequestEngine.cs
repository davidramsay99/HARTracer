using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harborer.Core.Engine;
using Harborer.Core.Http;

namespace Harborer.Net;

/// <summary>
/// The request engine. Sends a composed request over SocketsHttpHandler, one handler per hop, follows
/// redirects itself when asked (curl -L semantics), and records every hop as its own exchange with exact wire bytes,
/// timings and TLS details. Loaded by reflection through <see cref="RequestEngineLoader"/>; it holds no state, so one
/// instance can serve concurrent sends.
/// </summary>
public sealed class RequestEngine : IRequestEngine
{
    public Task<SendOutcome> SendAsync(HttpRequestSpec request, SendSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        // Snapshot the inputs so the composer can keep editing while the send runs off the calling thread.
        var spec = request.Clone();
        var settingsCopy = new SendSettings
        {
            ResponseSizeCap = settings.ResponseSizeCap,
            MaxRecordedWireBytes = settings.MaxRecordedWireBytes,
            FileBaseDirectory = settings.FileBaseDirectory,
        };
        return Task.Run(() => SendCoreAsync(spec, settingsCopy, cancellationToken), CancellationToken.None);
    }

    private static async Task<SendOutcome> SendCoreAsync(HttpRequestSpec spec, SendSettings settings, CancellationToken cancellationToken)
    {
        var outcome = new SendOutcome();
        long start = Stopwatch.GetTimestamp();

        Uri uri;
        PreparedBody? body;
        WebProxy? proxy;
        X509Certificate2? certificate = null;
        try
        {
            uri = Preflight.ParseUrl(spec.Url);
            _ = Preflight.NormalizeMethod(spec.Method);
            body = BodyBuilder.Prepare(spec, settings);
            proxy = Preflight.CreateProxy(spec.Options.Proxy);
            if (spec.Options.ClientCertificate is { } certificateSpec)
            {
                certificate = ClientCertificateLoader.Load(certificateSpec, settings.FileBaseDirectory);
            }
        }
        catch (RequestPreparationException ex)
        {
            certificate?.Dispose();
            outcome.Error = ex.Message;
            return outcome;
        }

        using (certificate)
        {
            SslStreamCertificateContext? certificateContext;
            try
            {
                // offline: true builds the client chain from local stores only, never fetching intermediates.
                certificateContext = certificate is null ? null : SslStreamCertificateContext.Create(certificate, null, offline: true);
            }
            catch (Exception ex) when (ex is CryptographicException or NotSupportedException or ArgumentException)
            {
                outcome.Error = $"Could not use the client certificate: {ex.Message}";
                return outcome;
            }

            using var timeoutSource = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var context = new SendContext(spec.Options, settings, proxy, certificateContext, cancellationToken, timeoutSource, start);
            if (spec.Options.Timeout is { } timeout && timeout > TimeSpan.Zero)
            {
                timeoutSource.CancelAfter(timeout.TotalMilliseconds >= int.MaxValue ? TimeSpan.FromMilliseconds(int.MaxValue - 1) : timeout);
            }

            var hop = HopRequest.First(spec, uri, body);
            int followed = 0;
            while (true)
            {
                var result = await ExchangeRunner.RunAsync(hop, context, linked.Token).ConfigureAwait(false);
                var record = result.Record;
                outcome.Exchanges.Add(record);
                if (record.Error is not null)
                {
                    outcome.Error = record.Error;
                    outcome.Cancelled = result.Cancelled;
                    break;
                }

                if (!spec.Options.FollowRedirects || !RedirectPolicy.IsFollowable(record.Status) ||
                    string.IsNullOrWhiteSpace(record.RedirectLocation))
                {
                    break;
                }

                // A negative maximum means unlimited, as with curl --max-redirs -1.
                int max = spec.Options.MaxRedirects;
                if (max >= 0 && followed >= max)
                {
                    outcome.Error = $"Maximum ({max}) redirects followed";
                    record.Notices.Add(outcome.Error);
                    break;
                }

                try
                {
                    hop = RedirectPolicy.Next(hop, uri, record.Status, record.RedirectLocation);
                }
                catch (RequestPreparationException ex)
                {
                    outcome.Error = ex.Message;
                    record.Notices.Add(ex.Message);
                    break;
                }

                followed++;
            }
        }

        return outcome;
    }
}
