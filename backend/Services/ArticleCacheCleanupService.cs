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
        // Enumerate all data files (non-.meta files) with their sizes and access times
        var files = Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".meta") && !f.EndsWith(".tmp"))
            .Select(f =>
            {
                var info = new FileInfo(f);
                return new { Path = f, info.Length, info.LastAccessTimeUtc, Hash = Path.GetFileName(f) };
            })
            .ToList();

        var totalSize = files.Sum(f => f.Length);
        if (totalSize <= maxSizeBytes) return;

        Log.Information(
            "Article cache size {SizeMb}MB exceeds max {MaxMb}MB, evicting oldest entries",
            totalSize / (1024 * 1024), maxSizeBytes / (1024 * 1024));

        // Sort by last access time ascending (oldest first)
        var sorted = files.OrderBy(f => f.LastAccessTimeUtc).ToList();
        var evictedHashes = new List<string>();

        foreach (var file in sorted)
        {
            if (totalSize <= targetSizeBytes) break;

            try
            {
                var metaPath = file.Path + ".meta";
                totalSize -= file.Length;

                File.Delete(file.Path);
                if (File.Exists(metaPath)) File.Delete(metaPath);

                evictedHashes.Add(file.Hash);

                // Clean up empty parent directory
                var parentDir = Path.GetDirectoryName(file.Path);
                TryDeleteEmptyDirectory(parentDir);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to evict cache file {Path}", file.Path);
            }
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
