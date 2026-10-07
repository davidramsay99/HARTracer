using System.Diagnostics;
using System.Globalization;
using System.Security.Authentication;

namespace HarLens.Net;

/// <summary>Turns an exception from a hop into a short, curl-like message. The connection trace knows best, so it is asked first.</summary>
internal static class ErrorDescriber
{
    public const string Cancelled = "Cancelled";

    public static (string Message, bool Cancelled) Describe(Exception exception, HopTrace trace, SendContext context, long bodyBytes)
    {
        if (context.UserToken.IsCancellationRequested)
        {
            return (Cancelled, true);
        }

        if (context.TimedOut)
        {
            long elapsed = (long)Stopwatch.GetElapsedTime(context.StartTimestamp).TotalMilliseconds;
            return (string.Create(
                CultureInfo.InvariantCulture,
                $"Operation timed out after {elapsed} milliseconds with {bodyBytes} bytes received"), false);
        }

        if (Find<TimeoutException>(exception) is not null && exception is OperationCanceledException or HttpRequestException)
        {
            long from = trace.ConnectStart != 0 ? trace.ConnectStart : trace.Start;
            long elapsed = (long)Stopwatch.GetElapsedTime(from).TotalMilliseconds;
            return (string.Create(CultureInfo.InvariantCulture, $"Connection timed out after {elapsed} milliseconds"), false);
        }

        if (trace.ConnectError is { } connectError)
        {
            return (connectError, false);
        }

        if (trace.CertificateError is { } certificateError)
        {
            return (certificateError, false);
        }

        string detail = Innermost(exception).Message;
        if (exception is HttpRequestException request)
        {
            string message = request.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => $"Could not resolve host: {trace.Request?.RequestUri?.Host}",
                HttpRequestError.ConnectionError => $"Connection failed: {detail}",
                HttpRequestError.SecureConnectionError => $"TLS handshake failed: {detail}",
                HttpRequestError.ResponseEnded => trace.Wire is { } wire && wire.Snapshot().FirstReadEnd == 0
                    ? "Empty reply from server"
                    : $"The server closed the connection before the response was complete: {detail}",
                HttpRequestError.InvalidResponse => $"Invalid HTTP response: {detail}",
                HttpRequestError.ProxyTunnelError => $"Proxy CONNECT failed: {detail}",
                HttpRequestError.HttpProtocolError => $"HTTP protocol error: {detail}",
                HttpRequestError.VersionNegotiationError => $"HTTP version negotiation failed: {detail}",
                HttpRequestError.ConfigurationLimitExceeded => $"Limit exceeded: {detail}",
                _ => request.Message == detail ? detail : $"{request.Message} {detail}",
            };
            return (message, false);
        }

        return exception switch
        {
            AuthenticationException => ($"TLS handshake failed: {detail}", false),
            IOException => ($"Connection error: {detail}", false),
            OperationCanceledException => ("The request was aborted", false),
            _ => ($"{exception.GetType().Name}: {exception.Message}", false),
        };
    }

    private static T? Find<T>(Exception exception)
        where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static Exception Innermost(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
