using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet.Cache;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Persistent article cache decorator. Uses sparse-file-per-media-file caching when
/// SparseFileCacheContext is available (streaming path), falling back to per-segment
/// file caching otherwise (health checks, queue processing).
/// </summary>
public class PersistentArticleCacheNntpClient : WrappingNntpClient
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cachedSegments = new();
    private readonly string _cacheDir;
    private readonly SparseFileManager _sparseFileManager;

    private record CacheEntry(
        UsenetYencHeader YencHeaders,
        bool HasArticleHeaders,
        UsenetArticleHeader? ArticleHeaders);

    private record CacheMetadata(
        UsenetYencHeader YencHeaders,
        UsenetArticleHeader? ArticleHeaders);

    public PersistentArticleCacheNntpClient(INntpClient usenetClient, ConfigManager configManager)
        : base(usenetClient)
    {
        _cacheDir = configManager.GetArticleCacheDir();
        Directory.CreateDirectory(_cacheDir);
        _sparseFileManager = new SparseFileManager(_cacheDir);
        LoadCacheMetadata();
    }

    public SparseFileManager SparseFileManager => _sparseFileManager;

    private void LoadCacheMetadata()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;

            var metaFiles = Directory.EnumerateFiles(_cacheDir, "*.meta", SearchOption.AllDirectories);
            var count = 0;
            foreach (var metaFile in metaFiles)
            {
                try
                {
                    var json = File.ReadAllText(metaFile);
                    var metadata = JsonSerializer.Deserialize<CacheMetadata>(json);
                    if (metadata == null) continue;

                    // Extract segment hash from filename (remove .meta extension)
                    var hash = Path.GetFileNameWithoutExtension(metaFile);
                    var dataFile = Path.Combine(Path.GetDirectoryName(metaFile)!, hash);
                    if (!File.Exists(dataFile)) continue;

                    _cachedSegments.TryAdd(hash, new CacheEntry(
                        YencHeaders: metadata.YencHeaders,
                        HasArticleHeaders: metadata.ArticleHeaders != null,
                        ArticleHeaders: metadata.ArticleHeaders));
                    count++;
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Failed to load cache metadata from {MetaFile}", metaFile);
                }
            }

            if (count > 0)
                Log.Information("Loaded {Count} entries from persistent article cache", count);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to scan persistent article cache directory");
        }
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var hash = GetHash(segmentId);
        var semaphore = _pendingRequests.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        try
        {
            var sparseCtx = cancellationToken.GetContext<SparseFileCacheContext>();

            // --- Sparse file path (streaming via WebDAV) ---
            if (sparseCtx != null)
            {
                var sparseFile = _sparseFileManager.GetOrCreate(sparseCtx.MediaFileId, sparseCtx.FileSize);

                // Check if segment is in sparse file
                if (sparseFile.TryGetSegmentMeta(hash, out var segMeta) && segMeta != null
                    && sparseFile.Ranges.IsPresent(segMeta.Offset, segMeta.Size))
                {
                    // Sparse cache hit — no disk I/O for LRU, just memory timestamp
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);

                    // Also keep in _cachedSegments for GetYencHeadersAsync
                    _cachedSegments.TryAdd(hash, new CacheEntry(segMeta.YencHeaders, false, null));

                    return new UsenetDecodedBodyResponse
                    {
                        SegmentId = segmentId,
                        ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                        ResponseMessage = "222 - Article retrieved from sparse cache",
                        Stream = new CachedYencStream(segMeta.YencHeaders, sparseFile.ReadSegment(segMeta.Offset, segMeta.Size))
                    };
                }

                // Check legacy per-segment cache — serve from there but also populate sparse file
                if (_cachedSegments.TryGetValue(hash, out var legacyEntry))
                {
                    TouchCacheFile(hash);
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    return ReadCachedBody(segmentId, hash, legacyEntry.YencHeaders);
                }

                // Sparse cache miss — download, write to sparse file
                // Hold a ref during the entire download+write to prevent eviction
                sparseFile.AddRef();
                try
                {
                    var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                        .ConfigureAwait(false);

                    await using var stream = response.Stream;
                    var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
                    if (yencHeaders == null)
                        throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");

                    await sparseFile.WriteFromStreamAsync(yencHeaders.PartOffset, stream, yencHeaders.PartSize, cancellationToken)
                        .ConfigureAwait(false);

                    var newSegMeta = new SparseSegmentMeta
                    {
                        Offset = yencHeaders.PartOffset,
                        Size = yencHeaders.PartSize,
                        YencHeaders = yencHeaders
                    };
                    sparseFile.SetSegmentMeta(hash, newSegMeta);

                    var entry = new CacheEntry(YencHeaders: yencHeaders, HasArticleHeaders: false, ArticleHeaders: null);
                    _cachedSegments.TryAdd(hash, entry);

                    // ReadSegment returns a SparseFileSubStream that holds its own ref
                    return new UsenetDecodedBodyResponse
                    {
                        SegmentId = segmentId,
                        ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                        ResponseMessage = "222 - Article cached in sparse file",
                        Stream = new CachedYencStream(yencHeaders, sparseFile.ReadSegment(yencHeaders.PartOffset, yencHeaders.PartSize))
                    };
                }
                finally
                {
                    sparseFile.Release();
                }
            }

            // --- Fallback: per-segment file path (health checks, queue processing) ---
            if (_cachedSegments.TryGetValue(hash, out var existingEntry))
            {
                TouchCacheFile(hash);
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                return ReadCachedBody(segmentId, hash, existingEntry.YencHeaders);
            }

            {
                var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                    .ConfigureAwait(false);

                await using var stream = response.Stream;
                var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
                if (yencHeaders == null)
                    throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");

                await CacheDecodedStreamAsync(hash, stream, cancellationToken).ConfigureAwait(false);

                var entry = new CacheEntry(
                    YencHeaders: yencHeaders,
                    HasArticleHeaders: false,
                    ArticleHeaders: null);

                WriteCacheMetadata(hash, new CacheMetadata(yencHeaders, null));
                _cachedSegments.TryAdd(hash, entry);

                return ReadCachedBody(segmentId, hash, yencHeaders);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var hash = GetHash(segmentId);
        var semaphore = _pendingRequests.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        try
        {
            var sparseCtx = cancellationToken.GetContext<SparseFileCacheContext>();

            // --- Sparse file path ---
            if (sparseCtx != null)
            {
                var sparseFile = _sparseFileManager.GetOrCreate(sparseCtx.MediaFileId, sparseCtx.FileSize);

                // Check if segment is in sparse file
                if (sparseFile.TryGetSegmentMeta(hash, out var segMeta) && segMeta != null
                    && sparseFile.Ranges.IsPresent(segMeta.Offset, segMeta.Size))
                {
                    // Check in-memory cache for existing article headers first
                    if (_cachedSegments.TryGetValue(hash, out var cachedEntry) && cachedEntry.HasArticleHeaders)
                    {
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                        return new UsenetDecodedArticleResponse
                        {
                            SegmentId = segmentId,
                            ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                            ResponseMessage = "220 - Article retrieved from sparse cache",
                            ArticleHeaders = cachedEntry.ArticleHeaders!,
                            Stream = new CachedYencStream(segMeta.YencHeaders, sparseFile.ReadSegment(segMeta.Offset, segMeta.Size))
                        };
                    }

                    // Ensure yenc headers are cached for GetYencHeadersAsync
                    _cachedSegments.TryAdd(hash, new CacheEntry(segMeta.YencHeaders, false, null));

                    // Body cached but no article headers — fetch headers separately
                    UsenetHeadResponse? headResponse = null;
                    try
                    {
                        headResponse = await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    }

                    var updatedEntry = new CacheEntry(segMeta.YencHeaders, true, headResponse.ArticleHeaders);
                    _cachedSegments.AddOrUpdate(hash, updatedEntry, (_, _) => updatedEntry);

                    return new UsenetDecodedArticleResponse
                    {
                        SegmentId = segmentId,
                        ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                        ResponseMessage = "220 - Article retrieved from sparse cache",
                        ArticleHeaders = headResponse.ArticleHeaders!,
                        Stream = new CachedYencStream(segMeta.YencHeaders, sparseFile.ReadSegment(segMeta.Offset, segMeta.Size))
                    };
                }

                // Check legacy per-segment cache
                if (_cachedSegments.TryGetValue(hash, out var legacyEntry))
                {
                    TouchCacheFile(hash);

                    if (legacyEntry.HasArticleHeaders)
                    {
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                        return ReadCachedArticle(segmentId, hash, legacyEntry.YencHeaders, legacyEntry.ArticleHeaders!);
                    }

                    UsenetHeadResponse? headResponse = null;
                    try
                    {
                        headResponse = await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    }

                    var updated = new CacheEntry(legacyEntry.YencHeaders, true, headResponse.ArticleHeaders);
                    _cachedSegments.TryUpdate(hash, updated, legacyEntry);
                    WriteCacheMetadata(hash, new CacheMetadata(legacyEntry.YencHeaders, headResponse.ArticleHeaders));

                    return ReadCachedArticle(segmentId, hash, legacyEntry.YencHeaders, headResponse.ArticleHeaders!);
                }

                // Sparse miss — download full article, write to sparse file
                // Hold a ref during the entire download+write to prevent eviction
                sparseFile.AddRef();
                try
                {
                    var response = await base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                        .ConfigureAwait(false);

                    await using var stream = response.Stream;
                    var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
                    if (yencHeaders == null)
                        throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");

                    await sparseFile.WriteFromStreamAsync(yencHeaders.PartOffset, stream, yencHeaders.PartSize, cancellationToken)
                        .ConfigureAwait(false);

                    var newSegMeta = new SparseSegmentMeta
                    {
                        Offset = yencHeaders.PartOffset,
                        Size = yencHeaders.PartSize,
                        YencHeaders = yencHeaders
                    };
                    sparseFile.SetSegmentMeta(hash, newSegMeta);

                    var newEntry = new CacheEntry(yencHeaders, true, response.ArticleHeaders);
                    _cachedSegments.TryAdd(hash, newEntry);

                    // ReadSegment returns a SparseFileSubStream that holds its own ref
                    return new UsenetDecodedArticleResponse
                    {
                        SegmentId = segmentId,
                        ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                        ResponseMessage = "220 - Article cached in sparse file",
                        ArticleHeaders = response.ArticleHeaders,
                        Stream = new CachedYencStream(yencHeaders, sparseFile.ReadSegment(yencHeaders.PartOffset, yencHeaders.PartSize))
                    };
                }
                finally
                {
                    sparseFile.Release();
                }
            }

            // --- Fallback: per-segment file path ---
            if (_cachedSegments.TryGetValue(hash, out var cacheEntry))
            {
                TouchCacheFile(hash);

                if (cacheEntry.HasArticleHeaders)
                {
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    return ReadCachedArticle(segmentId, hash, cacheEntry.YencHeaders, cacheEntry.ArticleHeaders!);
                }

                UsenetHeadResponse? headResponse = null;
                try
                {
                    headResponse = await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                }

                var updatedEntry = new CacheEntry(
                    YencHeaders: cacheEntry.YencHeaders,
                    HasArticleHeaders: true,
                    ArticleHeaders: headResponse.ArticleHeaders);

                _cachedSegments.TryUpdate(hash, updatedEntry, cacheEntry);
                WriteCacheMetadata(hash, new CacheMetadata(cacheEntry.YencHeaders, headResponse.ArticleHeaders));

                return ReadCachedArticle(segmentId, hash, cacheEntry.YencHeaders, headResponse.ArticleHeaders!);
            }

            {
                var response = await base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                    .ConfigureAwait(false);

                await using var stream = response.Stream;
                var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
                if (yencHeaders == null)
                    throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");

                await CacheDecodedStreamAsync(hash, stream, cancellationToken).ConfigureAwait(false);

                var newEntry = new CacheEntry(
                    YencHeaders: yencHeaders,
                    HasArticleHeaders: true,
                    ArticleHeaders: response.ArticleHeaders);

                WriteCacheMetadata(hash, new CacheMetadata(yencHeaders, response.ArticleHeaders));
                _cachedSegments.TryAdd(hash, newEntry);

                return ReadCachedArticle(segmentId, hash, yencHeaders, response.ArticleHeaders);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken)
    {
        var hash = GetHash(segmentId);
        var semaphore = _pendingRequests.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSegments.ContainsKey(hash))
            {
                TouchCacheFile(hash);
                return new UsenetExclusiveConnection(onConnectionReadyAgain: null);
            }

            // Also check sparse file cache
            var sparseCtx = cancellationToken.GetContext<SparseFileCacheContext>();
            if (sparseCtx != null && _sparseFileManager.TryGet(sparseCtx.MediaFileId, out var sparseFile) && sparseFile != null)
            {
                if (sparseFile.TryGetSegmentMeta(hash, out var segMeta) && segMeta != null
                    && sparseFile.Ranges.IsPresent(segMeta.Offset, segMeta.Size))
                {
                    return new UsenetExclusiveConnection(onConnectionReadyAgain: null);
                }
            }

            return await base.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        var hash = GetHash(segmentId);
        return _cachedSegments.TryGetValue(hash, out var existingEntry)
            ? Task.FromResult(existingEntry.YencHeaders)
            : base.GetYencHeadersAsync(segmentId, ct);
    }

    /// <summary>
    /// Remove evicted entries from the in-memory dictionary.
    /// Called by ArticleCacheCleanupService after deleting files.
    /// </summary>
    public void RemoveEntries(IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
        {
            _cachedSegments.TryRemove(hash, out _);
        }
    }

    private async Task CacheDecodedStreamAsync(string hash, YencStream stream, CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(hash);
        var directory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private void WriteCacheMetadata(string hash, CacheMetadata metadata)
    {
        try
        {
            var metaPath = GetCachePath(hash) + ".meta";
            var tmpPath = metaPath + ".tmp";
            var json = JsonSerializer.Serialize(metadata);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, metaPath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to write cache metadata for {Hash}", hash);
        }
    }

    private UsenetDecodedBodyResponse ReadCachedBody(string segmentId, string hash, UsenetYencHeader yencHeaders)
    {
        var cachePath = GetCachePath(hash);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        return new UsenetDecodedBodyResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "222 - Article retrieved from persistent cache",
            Stream = new CachedYencStream(yencHeaders, fileStream)
        };
    }

    private UsenetDecodedArticleResponse ReadCachedArticle(
        string segmentId, string hash, UsenetYencHeader yencHeaders, UsenetArticleHeader articleHeaders)
    {
        var cachePath = GetCachePath(hash);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        return new UsenetDecodedArticleResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            ResponseMessage = "220 - Article retrieved from persistent cache",
            ArticleHeaders = articleHeaders,
            Stream = new CachedYencStream(yencHeaders, fileStream)
        };
    }

    private void TouchCacheFile(string hash)
    {
        try
        {
            var cachePath = GetCachePath(hash);
            File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
        }
        catch
        {
            // Ignore — best-effort LRU tracking
        }
    }

    private string GetCachePath(string hash)
    {
        var prefix = hash[..2];
        return Path.Combine(_cacheDir, prefix, hash);
    }

    private static string GetHash(string segmentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        return Convert.ToHexString(hash);
    }

    public override void Dispose()
    {
        _sparseFileManager.Dispose();

        foreach (var semaphore in _pendingRequests.Values)
            semaphore.Dispose();

        _pendingRequests.Clear();
        _cachedSegments.Clear();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
