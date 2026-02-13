using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Cache;

/// <summary>
/// Manages sparse files for per-media-file caching. Each media file gets one sparse file on disk.
/// Segments are written at their actual byte offset. A ByteRangeTracker tracks which regions are present.
/// </summary>
public class SparseFileManager : IDisposable
{
    private readonly string _sparseDir;
    private readonly ConcurrentDictionary<Guid, SparseFile> _files = new();
    private readonly Timer _metaFlushTimer;
    private bool _disposed;

    public SparseFileManager(string cacheDir)
    {
        _sparseDir = Path.Combine(cacheDir, "sparse");
        Directory.CreateDirectory(_sparseDir);
        LoadExistingMetadata();
        _metaFlushTimer = new Timer(_ => FlushAllDirtyMetadata(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public SparseFile GetOrCreate(Guid mediaFileId, long fileSize)
    {
        return _files.GetOrAdd(mediaFileId, id => CreateSparseFile(id, fileSize));
    }

    public bool TryGet(Guid mediaFileId, out SparseFile? sparseFile)
    {
        return _files.TryGetValue(mediaFileId, out sparseFile);
    }

    /// <summary>
    /// Evict least-recently-used sparse files until total size is under targetBytes.
    /// Returns the list of evicted media file IDs.
    /// </summary>
    public List<Guid> EvictToSize(long targetBytes)
    {
        var evicted = new List<Guid>();
        var totalSize = _files.Values.Sum(f => f.Ranges.TotalCachedBytes);
        if (totalSize <= targetBytes) return evicted;

        var sorted = _files.Values
            .Where(f => f.RefCount == 0) // don't evict files with active streams
            .OrderBy(f => f.LastAccessUtc)
            .ToList();

        foreach (var file in sorted)
        {
            if (totalSize <= targetBytes) break;
            totalSize -= file.Ranges.TotalCachedBytes;
            RemoveFile(file.MediaFileId);
            evicted.Add(file.MediaFileId);
        }

        return evicted;
    }

    public void RemoveFile(Guid mediaFileId)
    {
        if (!_files.TryRemove(mediaFileId, out var file)) return;
        file.Close();
        var dir = GetMediaDir(mediaFileId);
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to delete sparse file directory {Dir}", dir);
        }
    }

    /// <summary>
    /// Get total cached bytes across all sparse files.
    /// </summary>
    public long TotalCachedBytes => _files.Values.Sum(f => f.Ranges.TotalCachedBytes);

    /// <summary>
    /// Get all tracked media file IDs (for cleanup service).
    /// </summary>
    public ICollection<Guid> TrackedMediaFileIds => _files.Keys;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _metaFlushTimer.Dispose();
        FlushAllDirtyMetadata();
        foreach (var file in _files.Values)
            file.Close();
        _files.Clear();
        GC.SuppressFinalize(this);
    }

    private SparseFile CreateSparseFile(Guid mediaFileId, long fileSize)
    {
        var dir = GetMediaDir(mediaFileId);
        Directory.CreateDirectory(dir);
        var dataPath = Path.Combine(dir, "data");

        var handle = File.OpenHandle(
            dataPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        // Truncate/extend to full file size to make it sparse
        RandomAccess.SetLength(handle, fileSize);

        var sparseFile = new SparseFile(mediaFileId, handle, dataPath, fileSize, new ByteRangeTracker(),
            new ConcurrentDictionary<string, SparseSegmentMeta>());
        return sparseFile;
    }

    private string GetMediaDir(Guid mediaFileId) => Path.Combine(_sparseDir, mediaFileId.ToString("N"));

    private void LoadExistingMetadata()
    {
        if (!Directory.Exists(_sparseDir)) return;

        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(_sparseDir))
        {
            try
            {
                var metaPath = Path.Combine(dir, "meta.json");
                var dataPath = Path.Combine(dir, "data");
                if (!File.Exists(metaPath) || !File.Exists(dataPath)) continue;

                var json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<SparseFileMeta>(json);
                if (meta == null) continue;

                var dirName = Path.GetFileName(dir);
                if (!Guid.TryParse(dirName, out var mediaFileId)) continue;

                var ranges = meta.Ranges
                    .Where(r => r.Length >= 2)
                    .Select(r => (r[0], r[1]))
                    .ToList();
                var tracker = ByteRangeTracker.FromRanges(ranges);

                var segments = new ConcurrentDictionary<string, SparseSegmentMeta>(
                    meta.Segments ?? new Dictionary<string, SparseSegmentMeta>());

                var handle = File.OpenHandle(
                    dataPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);

                var sparseFile = new SparseFile(mediaFileId, handle, dataPath, meta.FileSize, tracker, segments);
                sparseFile.LastAccessUtc = meta.LastAccess;
                _files.TryAdd(mediaFileId, sparseFile);
                count++;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to load sparse file metadata from {Dir}", dir);
            }
        }

        if (count > 0)
            Log.Information("Loaded {Count} sparse cache files", count);
    }

    private void FlushAllDirtyMetadata()
    {
        foreach (var file in _files.Values)
        {
            if (!file.IsDirty) continue;
            try
            {
                var dir = GetMediaDir(file.MediaFileId);
                var metaPath = Path.Combine(dir, "meta.json");
                var tmpPath = metaPath + ".tmp";

                var meta = new SparseFileMeta
                {
                    FileSize = file.FileSize,
                    LastAccess = file.LastAccessUtc,
                    Ranges = file.Ranges.GetRanges().Select(r => new[] { r.Start, r.End }).ToList(),
                    Segments = file.GetSegments()
                };

                var json = JsonSerializer.Serialize(meta);
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, metaPath, overwrite: true);

                // Clear dirty flag only after successful write
                file.IsDirty = false;
            }
            catch (Exception e)
            {
                // IsDirty remains true — will retry on next flush
                Log.Warning(e, "Failed to flush sparse file metadata for {Id}", file.MediaFileId);
            }
        }
    }
}

public class SparseFile
{
    private SafeFileHandle? _handle;
    private readonly string _dataPath;
    private readonly ConcurrentDictionary<string, SparseSegmentMeta> _segments;
    private int _refCount;

    public Guid MediaFileId { get; }
    public long FileSize { get; }
    public ByteRangeTracker Ranges { get; }
    public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
    public volatile bool IsDirty;
    public int RefCount => _refCount;

    internal SparseFile(
        Guid mediaFileId,
        SafeFileHandle handle,
        string dataPath,
        long fileSize,
        ByteRangeTracker ranges,
        ConcurrentDictionary<string, SparseSegmentMeta> segments)
    {
        MediaFileId = mediaFileId;
        _handle = handle;
        _dataPath = dataPath;
        FileSize = fileSize;
        Ranges = ranges;
        _segments = segments;
    }

    public void AddRef() => Interlocked.Increment(ref _refCount);
    public void Release() => Interlocked.Decrement(ref _refCount);

    /// <summary>
    /// Write decoded segment data at its byte offset in the sparse file.
    /// </summary>
    public async ValueTask WriteSegmentAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var handle = _handle;
        if (handle == null || handle.IsClosed) throw new ObjectDisposedException(nameof(SparseFile));
        await RandomAccess.WriteAsync(handle, data, offset, ct).ConfigureAwait(false);
        Ranges.AddRange(offset, data.Length);
        LastAccessUtc = DateTime.UtcNow;
        IsDirty = true;
    }

    /// <summary>
    /// Write from a stream at the given offset. Used for CopyToAsync pattern from YencStream.
    /// </summary>
    public async ValueTask WriteFromStreamAsync(long offset, Stream source, long length, CancellationToken ct)
    {
        var handle = _handle;
        if (handle == null || handle.IsClosed) throw new ObjectDisposedException(nameof(SparseFile));

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var written = 0L;
            while (written < length)
            {
                var toRead = (int)Math.Min(buffer.Length, length - written);
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (bytesRead == 0) break;
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, bytesRead), offset + written, ct)
                    .ConfigureAwait(false);
                written += bytesRead;
            }

            Ranges.AddRange(offset, written);
            LastAccessUtc = DateTime.UtcNow;
            IsDirty = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Create a read-only stream positioned at offset, limited to length bytes.
    /// The returned stream holds a reference on this SparseFile (via AddRef/Release)
    /// to prevent the file handle from being closed during reads.
    /// </summary>
    public Stream ReadSegment(long offset, long length)
    {
        var handle = _handle;
        if (handle == null || handle.IsClosed) throw new ObjectDisposedException(nameof(SparseFile));
        LastAccessUtc = DateTime.UtcNow;
        // SparseFileSubStream calls AddRef in its constructor and Release on Dispose
        return new SparseFileSubStream(this, handle, offset, length);
    }

    /// <summary>
    /// Store yenc headers for a segment (keyed by segment hash).
    /// </summary>
    public void SetSegmentMeta(string segmentHash, SparseSegmentMeta meta)
    {
        _segments[segmentHash] = meta;
        IsDirty = true;
    }

    /// <summary>
    /// Try to get stored yenc headers for a segment.
    /// </summary>
    public bool TryGetSegmentMeta(string segmentHash, out SparseSegmentMeta? meta)
    {
        return _segments.TryGetValue(segmentHash, out meta);
    }

    internal Dictionary<string, SparseSegmentMeta> GetSegments() => new(_segments);

    internal void Close()
    {
        Ranges.Dispose();
        var handle = Interlocked.Exchange(ref _handle, null);
        handle?.Dispose();
    }
}

public class SparseSegmentMeta
{
    public long Offset { get; set; }
    public long Size { get; set; }
    public UsenetYencHeader YencHeaders { get; set; } = null!;
}

/// <summary>
/// A read-only Stream backed by RandomAccess reads into a sparse file handle.
/// Holds a reference on the owning SparseFile to prevent handle disposal during reads.
/// </summary>
internal class SparseFileSubStream : Stream
{
    private readonly SparseFile _owner;
    private readonly SafeFileHandle _handle;
    private readonly long _startOffset;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    public SparseFileSubStream(SparseFile owner, SafeFileHandle handle, long startOffset, long length)
    {
        _owner = owner;
        _owner.AddRef();
        _handle = handle;
        _startOffset = startOffset;
        _length = length;
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, remaining);
        var bytesRead = RandomAccess.Read(_handle, buffer.AsSpan(offset, toRead), _startOffset + _position);
        _position += bytesRead;

        // Drop page cache for consumed region to prevent RAM bloat on large streams
        if (bytesRead > 0 && _position >= _length)
            LinuxNative.DropPageCache(_handle, _startOffset, _length);

        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var bytesRead = await RandomAccess.ReadAsync(_handle, buffer[..toRead], _startOffset + _position, cancellationToken).ConfigureAwait(false);
        _position += bytesRead;

        // Drop page cache for consumed region to prevent RAM bloat on large streams
        if (bytesRead > 0 && _position >= _length)
            LinuxNative.DropPageCache(_handle, _startOffset, _length);

        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        _position = Math.Clamp(_position, 0, _length);
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
                _owner.Release();
        }
        base.Dispose(disposing);
    }
}

internal class SparseFileMeta
{
    public long FileSize { get; set; }
    public DateTime LastAccess { get; set; }
    public List<long[]> Ranges { get; set; } = [];
    public Dictionary<string, SparseSegmentMeta>? Segments { get; set; }
}
