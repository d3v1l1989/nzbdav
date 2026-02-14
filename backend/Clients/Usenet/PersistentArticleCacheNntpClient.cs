using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Persistent article cache decorator. Stores decoded article bytes to disk
/// keyed by SHA256 of segment ID. Cache hits skip the download semaphore entirely.
/// </summary>
public class PersistentArticleCacheNntpClient : WrappingNntpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cachedSegments = new();
    private readonly string _cacheDir;

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
        LoadCacheMetadata();
    }

    private void LoadCacheMetadata()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;

            var metaFiles = Directory.EnumerateFiles(_cacheDir, "*.meta", SearchOption.AllDirectories);
            var count = 0;
            var skipped = 0;
            foreach (var metaFile in metaFiles)
            {
                try
                {
                    var json = File.ReadAllText(metaFile);
                    var metadata = JsonSerializer.Deserialize<CacheMetadata>(json, JsonOptions);
                    if (metadata == null) continue;

                    // Skip entries with missing yenc field data (caused by
                    // pre-fix serialization that dropped public fields).
                    // These will be re-fetched from usenet on next access.
                    if (metadata.YencHeaders.PartSize == 0)
                    {
                        skipped++;
                        continue;
                    }

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
                catch (JsonException)
                {
                    // Old .meta files are missing required yenc fields
                    // (pre-fix serialization dropped public fields).
                    // Silently skip — they'll be re-fetched on next access.
                    skipped++;
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Failed to load cache metadata from {MetaFile}", metaFile);
                }
            }

            if (count > 0 || skipped > 0)
                Log.Information("Loaded {Count} entries from persistent article cache (skipped {Skipped} entries with missing yenc metadata)", count, skipped);
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
            // Check if already cached
            if (_cachedSegments.TryGetValue(hash, out var existingEntry))
            {
                TouchCacheFile(hash);
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                return ReadCachedBody(segmentId, hash, existingEntry.YencHeaders);
            }

            // Fetch and cache the body
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
            // Check if already cached with headers
            if (_cachedSegments.TryGetValue(hash, out var cacheEntry))
            {
                TouchCacheFile(hash);

                if (cacheEntry.HasArticleHeaders)
                {
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    return ReadCachedArticle(segmentId, hash, cacheEntry.YencHeaders, cacheEntry.ArticleHeaders!);
                }

                // Only body is cached, fetch article headers separately
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

            // Fetch and cache the full article
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
            var json = JsonSerializer.Serialize(metadata, JsonOptions);
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
        foreach (var semaphore in _pendingRequests.Values)
            semaphore.Dispose();

        _pendingRequests.Clear();
        _cachedSegments.Clear();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
