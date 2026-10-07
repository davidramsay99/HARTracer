using System.Diagnostics;

namespace Harborer.Net;

/// <summary>What a <see cref="WireRecorder"/> saw, taken once the connection is finished with.</summary>
internal sealed record WireSnapshot(
    byte[] Sent,
    byte[] Received,
    bool Truncated,
    long FirstWriteStart,
    long LastWriteEnd,
    long LastWriteEndBeforeFirstRead,
    long FirstReadEnd,
    long LastReadEnd);

/// <summary>
/// Copies the plaintext bytes of one connection, capped per direction, and timestamps the first write, the last
/// write before the first read, the first read and the last read. Timestamps are Stopwatch ticks; 0 means "not seen".
/// </summary>
internal sealed class WireRecorder
{
    private readonly object _gate = new();
    private readonly int _cap;
    private readonly MemoryStream? _sent;
    private readonly MemoryStream? _received;
    private bool _truncated;
    private long _firstWriteStart;
    private long _lastWriteEnd;
    private long _lastWriteEndBeforeFirstRead;
    private long _firstReadEnd;
    private long _lastReadEnd;

    /// <param name="cap">Bytes kept per direction.</param>
    /// <param name="capture">False keeps timestamps only (used for the proxy CONNECT connection).</param>
    public WireRecorder(int cap, bool capture = true)
    {
        _cap = Math.Max(0, cap);
        if (capture)
        {
            _sent = new MemoryStream();
            _received = new MemoryStream();
        }
    }

    public long LastWriteEnd
    {
        get
        {
            lock (_gate)
            {
                return _lastWriteEnd;
            }
        }
    }

    /// <returns>True when no read had completed yet; pass it to <see cref="OnWriteCompleted"/>.</returns>
    public bool OnWriteStarting(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_firstWriteStart == 0)
            {
                _firstWriteStart = Stopwatch.GetTimestamp();
            }

            Append(_sent, data);
            return _firstReadEnd == 0;
        }
    }

    /// <param name="startedBeforeFirstRead">
    /// From <see cref="OnWriteStarting"/>. A read can be pending while the request is written, and on a fast
    /// server the response may complete it before this write's continuation runs; such a write still belongs to
    /// the send phase.
    /// </param>
    public void OnWriteCompleted(bool startedBeforeFirstRead)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            _lastWriteEnd = now;
            if (startedBeforeFirstRead)
            {
                _lastWriteEndBeforeFirstRead = Math.Max(_lastWriteEndBeforeFirstRead, now);
            }
        }
    }

    public void OnRead(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_firstReadEnd == 0)
            {
                _firstReadEnd = now;
            }

            _lastReadEnd = now;
            Append(_received, data);
        }
    }

    public WireSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new WireSnapshot(
                _sent?.ToArray() ?? [],
                _received?.ToArray() ?? [],
                _truncated,
                _firstWriteStart,
                _lastWriteEnd,
                _lastWriteEndBeforeFirstRead,
                _firstReadEnd,
                _lastReadEnd);
        }
    }

    private void Append(MemoryStream? target, ReadOnlySpan<byte> data)
    {
        if (target is null || data.IsEmpty)
        {
            return;
        }

        int room = _cap - (int)target.Length;
        if (data.Length > room)
        {
            _truncated = true;
            data = data[..Math.Max(0, room)];
        }

        target.Write(data);
    }
}

/// <summary>Passes every read and write through to the connection's plaintext stream and reports it to a recorder.</summary>
internal sealed class RecordingStream : Stream
{
    private readonly Stream _inner;
    private readonly WireRecorder _recorder;

    public RecordingStream(Stream inner, WireRecorder recorder)
    {
        _inner = inner;
        _recorder = recorder;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanWrite => _inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = _inner.Read(buffer);
        if (read > 0)
        {
            _recorder.OnRead(buffer[..read]);
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _recorder.OnRead(buffer.Span[..read]);
        }

        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        bool beforeFirstRead = _recorder.OnWriteStarting(buffer);
        _inner.Write(buffer);
        _recorder.OnWriteCompleted(beforeFirstRead);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        bool beforeFirstRead = _recorder.OnWriteStarting(buffer.Span);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _recorder.OnWriteCompleted(beforeFirstRead);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
