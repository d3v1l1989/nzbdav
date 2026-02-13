using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using NWebDav.Server.Helpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next)
{
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> _recentMissingArticles = new();
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> _recentConnectionLimitErrors = new();
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);
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
            Log.Error("File {FilePath} could not seek to byte position {SeekPosition}", filePath, seekPosition);
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
    }
}
