using HarLens.Core.Http;

namespace HarLens.Core.Engine;

/// <summary>
/// The request engine contract. The only implementation lives in HarLens.Net, which the application loads
/// through <see cref="RequestEngineLoader"/> the first time Send is pressed with Offline Mode OFF.
/// </summary>
public interface IRequestEngine
{
    Task<SendOutcome> SendAsync(HttpRequestSpec request, SendSettings settings, CancellationToken cancellationToken);
}

public sealed class SendSettings
{
    /// <summary>Response bodies beyond this many bytes are truncated with a notice (default 100 MB).</summary>
    public long ResponseSizeCap { get; set; } = 100L * 1024 * 1024;

    /// <summary>Upper bound on the exact wire bytes kept per direction.</summary>
    public int MaxRecordedWireBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Directory that relative file references (multipart files, binary bodies) resolve against.</summary>
    public string? FileBaseDirectory { get; set; }
}
