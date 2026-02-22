using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that periodically evicts the oldest cached articles
/// when the persistent article cache exceeds the configured maximum size.
/// </summary>
public class ArticleCacheCleanupService(
    ConfigManager configManager,
    UsenetStreamingClient streamingClient
) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken).ConfigureAwait(false);

                if (!configManager.IsArticleCacheEnabled()) continue;

                var maxSizeGb = configManager.GetArticleCacheMaxSizeGb();
                if (maxSizeGb <= 0) continue;

                var cacheDir = configManager.GetArticleCacheDir();
                if (!Directory.Exists(cacheDir)) continue;

                var maxSizeBytes = (long)maxSizeGb * 1024 * 1024 * 1024;
                var targetSizeBytes = (long)(maxSizeBytes * 0.9);

                EvictIfOverSize(cacheDir, maxSizeBytes, targetSizeBytes, streamingClient);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error(e, "Error during article cache cleanup: {Message}", e.Message);
            }
        }
    }

    private static void EvictIfOverSize(
        string cacheDir, long maxSizeBytes, long targetSizeBytes,
        UsenetStreamingClient streamingClient)
    {
        // Phase 1: Calculate total size with streaming enumeration (no materialization)
        long totalSize = 0;
        foreach (var f in Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".meta") || f.EndsWith(".tmp")) continue;
            try { totalSize += new FileInfo(f).Length; }
            catch { /* file may have been deleted concurrently */ }
        }

        if (totalSize <= maxSizeBytes) return;

        Log.Information(
            "Article cache size {SizeMb}MB exceeds max {MaxMb}MB, evicting oldest entries",
            totalSize / (1024 * 1024), maxSizeBytes / (1024 * 1024));

        // Phase 2: Evict shard-by-shard to avoid loading millions of FileInfo at once
        var evictedHashes = new List<string>();
        string[] shardDirs;
        try
        {
            shardDirs = Directory.GetDirectories(cacheDir);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to enumerate cache shard directories");
            return;
        }

        foreach (var shardDir in shardDirs)
        {
            if (totalSize <= targetSizeBytes) break;

            List<(string Path, long Length, DateTime LastAccessTimeUtc, string Hash)> shardFiles;
            try
            {
                shardFiles = Directory.EnumerateFiles(shardDir)
                    .Where(f => !f.EndsWith(".meta") && !f.EndsWith(".tmp"))
                    .Select(f =>
                    {
                        var info = new FileInfo(f);
                        return (f, info.Length, info.LastAccessTimeUtc, Hash: System.IO.Path.GetFileName(f));
                    })
                    .OrderBy(f => f.LastAccessTimeUtc)
                    .ToList();
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to enumerate shard directory {ShardDir}", shardDir);
                continue;
            }

            foreach (var file in shardFiles)
            {
                if (totalSize <= targetSizeBytes) break;

                try
                {
                    var metaPath = file.Path + ".meta";
                    totalSize -= file.Length;

                    File.Delete(file.Path);
                    if (File.Exists(metaPath)) File.Delete(metaPath);

                    evictedHashes.Add(file.Hash);
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Failed to evict cache file {Path}", file.Path);
                }
            }

            TryDeleteEmptyDirectory(shardDir);
        }

        if (evictedHashes.Count > 0)
        {
            // Notify the cache client to remove evicted entries from memory
            if (streamingClient is WrappingNntpClient wrapper)
            {
                var cacheClient = FindPersistentCacheClient(wrapper);
                cacheClient?.RemoveEntries(evictedHashes);
            }

            Log.Information("Evicted {Count} entries from article cache", evictedHashes.Count);
        }
    }

    private static PersistentArticleCacheNntpClient? FindPersistentCacheClient(WrappingNntpClient wrapper)
    {
        // Walk the decorator chain to find our cache client
        var current = wrapper;
        while (current != null)
        {
            if (current is PersistentArticleCacheNntpClient cacheClient)
                return cacheClient;

            // Access the underlying client via reflection since _usenetClient is private
            var field = typeof(WrappingNntpClient).GetField("_usenetClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var inner = field?.GetValue(current) as INntpClient;

            if (inner is WrappingNntpClient wrappingInner)
                current = wrappingInner;
            else
                break;
        }

        return null;
    }

    private static void TryDeleteEmptyDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, recursive: false);
        }
        catch
        {
            // Ignore — best-effort cleanup
        }
    }
}
