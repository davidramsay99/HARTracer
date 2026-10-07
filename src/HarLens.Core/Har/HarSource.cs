using Microsoft.Win32.SafeHandles;

namespace HarLens.Core.Har;

/// <summary>
/// The bytes behind a loaded HAR. The index keeps byte offsets into a source and reads bodies and raw entries
/// back on demand (SPEC 5.3), so the full document is never materialized.
/// </summary>
public abstract class HarSource : IDisposable
{
    private static int s_nextId;

    protected HarSource(string displayName, string? filePath)
    {
        DisplayName = displayName;
        FilePath = filePath;
        Id = Interlocked.Increment(ref s_nextId);
    }

    /// <summary>Process-unique identifier, used for cache keys.</summary>
    public int Id { get; }

    /// <summary>File name or a label such as "Composer".</summary>
    public string DisplayName { get; }

    /// <summary>Full path of the file the source was read from, or null for in-memory sources.</summary>
    public string? FilePath { get; }

    /// <summary>True when the file on disk was gzip-compressed and the source holds the decompressed bytes in memory.</summary>
    public bool WasCompressed { get; init; }

    public abstract long Length { get; }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes starting at <paramref name="offset"/>.</summary>
    public abstract void ReadAt(long offset, Span<byte> destination);

    /// <summary>Opens a forward-only stream over the whole source, used by the parser.</summary>
    public abstract Stream OpenSequential();

    public byte[] ReadBytes(long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Read beyond the end of the HAR source.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(count);
        ReadAt(offset, bytes);
        return bytes;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    public override string ToString() => DisplayName;
}

/// <summary>A HAR file on disk read with positional reads. The file is opened with read sharing only, so it cannot change underneath the index.</summary>
public sealed class FileHarSource : HarSource
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;

    public FileHarSource(string path)
        : base(Path.GetFileName(path), Path.GetFullPath(path))
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        _length = RandomAccess.GetLength(_handle);
    }

    public override long Length => _length;

    public override void ReadAt(long offset, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of HAR file.");
            }

            total += read;
        }
    }

    public override Stream OpenSequential() =>
        new FileStream(FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }
    }
}

/// <summary>
/// Bytes held in memory in fixed-size chunks (no single array above 2 GB, no large-object-heap churn).
/// Used for gzip-compressed files, SAZ imports, merged and composer sessions. Appends are thread-safe.
/// </summary>
public sealed class MemoryHarSource : HarSource
{
    private const int ChunkSize = 16 * 1024 * 1024;
    private readonly List<byte[]> _chunks = [];
    private readonly Lock _lock = new();
    private long _length;

    public MemoryHarSource(string displayName, string? filePath = null)
        : base(displayName, filePath)
    {
    }

    public MemoryHarSource(string displayName, ReadOnlySpan<byte> bytes, string? filePath = null)
        : base(displayName, filePath)
    {
        Append(bytes);
    }

    public override long Length
    {
        get
        {
            lock (_lock)
            {
                return _length;
            }
        }
    }

    /// <summary>Appends bytes and returns the offset of the first appended byte.</summary>
    public long Append(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            var start = _length;
            while (!bytes.IsEmpty)
            {
                var chunkIndex = (int)(_length / ChunkSize);
                var within = (int)(_length % ChunkSize);
                if (chunkIndex == _chunks.Count)
                {
                    _chunks.Add(GC.AllocateUninitializedArray<byte>(ChunkSize));
                }

                var take = Math.Min(bytes.Length, ChunkSize - within);
                bytes[..take].CopyTo(_chunks[chunkIndex].AsSpan(within));
                bytes = bytes[take..];
                _length += take;
            }

            return start;
        }
    }

    /// <summary>Copies a whole stream into the source, for example a gzip decompression stream.</summary>
    public void AppendFrom(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(buffer.AsSpan(0, read));
        }
    }

    public override void ReadAt(long offset, Span<byte> destination)
    {
        lock (_lock)
        {
            if (offset < 0 || offset + destination.Length > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            while (!destination.IsEmpty)
            {
                var chunkIndex = (int)(offset / ChunkSize);
                var within = (int)(offset % ChunkSize);
                var take = Math.Min(destination.Length, ChunkSize - within);
                _chunks[chunkIndex].AsSpan(within, take).CopyTo(destination);
                destination = destination[take..];
                offset += take;
            }
        }
    }

    public override Stream OpenSequential() => new ChunkStream(this);

    private sealed class ChunkStream(MemoryHarSource owner) : Stream
    {
        private readonly long _length = owner.Length;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = (int)Math.Min(buffer.Length, _length - _position);
            if (take <= 0)
            {
                return 0;
            }

            owner.ReadAt(_position, buffer[..take]);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
