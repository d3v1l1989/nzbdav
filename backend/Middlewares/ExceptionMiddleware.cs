using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using NWebDav.Server.Helpers;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next, ConfigManager configManager)
{
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> _recentMissingArticles = new();
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> _recentConnectionLimitErrors = new();
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> _recentSeekErrors = new();
    private static readonly ConcurrentDictionary<Guid, DateTime> _recentRepairTriggers = new();
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RepairDedupeWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromMinutes(5);
    private static int _callCount;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (IsCausedByAbortedRequest(e, context))
        {
            // If the response has not started, we can write our custom response
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 499; // Non-standard status code for client closed request
                await context.Response.WriteAsync("Client closed request.").ConfigureAwait(false);
            }
        }
        catch (UsenetArticleNotFoundException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var dedupeKey = $"{filePath}|{e.SegmentId}";
            LogWithDedup(_recentMissingArticles, dedupeKey, suppressed =>
            {
                if (suppressed > 0)
                    Log.Error("File {FilePath} has missing articles: {ErrorMessage} (suppressed {SuppressedCount} duplicates in last 60s)",
                        filePath, e.Message, suppressed);
                else
                    Log.Error("File {FilePath} has missing articles: {ErrorMessage}", filePath, e.Message);
            });

            if (context.Items["DavItem"] is DavItem davItem)
                ScheduleRepair(davItem.Id);
        }
        catch (CouldNotLoginToUsenetException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 503;
            }

            var filePath = GetRequestFilePath(context);
            var msg = e.Message;
            var innerMsg = e.InnerException?.Message;
            var errorDetail = innerMsg ?? msg;
            var isConnectionLimit = errorDetail.Contains("connection limit", StringComparison.OrdinalIgnoreCase);
            if (isConnectionLimit)
            {
                LogConnectionLimitError(errorDetail);
            }
            else
            {
                Log.Error("File {FilePath} provider authentication failed: {ErrorMessage}", filePath, errorDetail);
            }
        }
        catch (CouldNotConnectToUsenetException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 503;
            }

            var filePath = GetRequestFilePath(context);
            Log.Error("File {FilePath} provider connection failed: {ErrorMessage}", filePath, e.InnerException?.Message ?? e.Message);
        }
        catch (SeekPositionNotFoundException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "unknown";
            var dedupeKey = $"{filePath}|{seekPosition}";
            LogWithDedup(_recentSeekErrors, dedupeKey, suppressed =>
            {
                if (suppressed > 0)
                    Log.Error("File {FilePath} could not seek to byte position {SeekPosition} (suppressed {SuppressedCount} duplicates in last 60s)",
                        filePath, seekPosition, suppressed);
                else
                    Log.Error("File {FilePath} could not seek to byte position {SeekPosition}", filePath, seekPosition);
            });
        }
        catch (Exception e) when (IsDavItemRequest(context))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 500;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "0";
            Log.Error("File {FilePath} read error at byte {SeekPosition}: {ErrorType}: {ErrorMessage}",
                filePath, seekPosition, e.GetType().Name, e.Message);
        }
    }

    private bool IsCausedByAbortedRequest(Exception e, HttpContext context)
    {
        var isAffectedException = e is OperationCanceledException or EndOfStreamException;
        var isRequestAborted = context.RequestAborted.IsCancellationRequested ||
                               SigtermUtil.GetCancellationToken().IsCancellationRequested;
        return isAffectedException && isRequestAborted;
    }

    private static string GetRequestFilePath(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem davItem
            ? davItem.Path
            : context.Request.Path;
    }

    private static bool IsDavItemRequest(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem;
    }

    private static void LogConnectionLimitError(string errorMessage)
    {
        LogWithDedup(_recentConnectionLimitErrors, errorMessage, suppressed =>
        {
            if (suppressed > 0)
                Log.Warning("Provider connection limit reached: {ErrorMessage} (suppressed {SuppressedCount} duplicates in last 60s)",
                    errorMessage, suppressed);
            else
                Log.Warning("Provider connection limit reached: {ErrorMessage}", errorMessage);
        });
    }

    /// <summary>
    /// Deduplicate log messages by key within a 60-second window.
    /// Logs on first occurrence and when the window expires, reporting
    /// how many duplicates were suppressed in the previous window.
    /// </summary>
    private static void LogWithDedup(
        ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> tracker,
        string dedupeKey,
        Action<int> logAction)
    {
        var now = DateTime.UtcNow;
        var shouldLog = false;
        var suppressedCount = 0;

        tracker.AddOrUpdate(
            dedupeKey,
            _ => { shouldLog = true; return (now, 0); },
            (_, existing) =>
            {
                if (now - existing.LastLogged > DedupeWindow)
                {
                    shouldLog = true;
                    suppressedCount = existing.SuppressedCount;
                    return (now, 0);
                }
                return (existing.LastLogged, existing.SuppressedCount + 1);
            }
        );

        if (shouldLog)
            logAction(suppressedCount);

        CleanupStaleEntries();
    }

    private void ScheduleRepair(Guid davItemId)
    {
        if (!configManager.IsRepairJobEnabled())
            return;

        var now = DateTime.UtcNow;
        var isDuplicate = false;
        _recentRepairTriggers.AddOrUpdate(
            davItemId,
            _ => now,
            (_, existing) =>
            {
                if (now - existing < RepairDedupeWindow)
                {
                    isDuplicate = true;
                    return existing;
                }
                return now;
            }
        );

        if (isDuplicate)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();
                var item = await dbContext.Items.FindAsync(davItemId).ConfigureAwait(false);
                if (item == null)
                    return;

                // Use UnixEpoch as a sentinel so dynamic repairs sort first in the
                // HealthCheckService queue (which orders non-NULL before NULL, then by
                // NextHealthCheck ascending). This beats any real timestamp.
                var urgent = DateTimeOffset.UnixEpoch;
                if (item.NextHealthCheck == urgent)
                    return;

                item.NextHealthCheck = urgent;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
                Log.Information("Scheduled dynamic repair for {FilePath}", item.Path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to schedule dynamic repair for DavItem {DavItemId}", davItemId);
            }
        });
    }

    private static void CleanupStaleEntries()
    {
        if (Interlocked.Increment(ref _callCount) % 100 != 0)
            return;

        var cutoff = DateTime.UtcNow - CleanupThreshold;
        foreach (var kvp in _recentMissingArticles)
        {
            if (kvp.Value.LastLogged < cutoff)
                _recentMissingArticles.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _recentConnectionLimitErrors)
        {
            if (kvp.Value.LastLogged < cutoff)
                _recentConnectionLimitErrors.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _recentSeekErrors)
        {
            if (kvp.Value.LastLogged < cutoff)
                _recentSeekErrors.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _recentRepairTriggers)
        {
            if (kvp.Value < cutoff)
                _recentRepairTriggers.TryRemove(kvp.Key, out _);
        }
    }
}
