using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class RemoveUnlinkedFilesTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager,
    bool isDryRun
) : BaseTask
{
    private static List<string> _allRemovedPaths = [];

    protected override async Task ExecuteInternal()
    {
        try
        {
            await RemoveUnlinkedFiles().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to remove unlinked files.");
        }
    }

    private async Task RemoveUnlinkedFiles()
    {
        var removedItems = new HashSet<Guid>();
        var dryRun = isDryRun ? "Dry Run - " : string.Empty;

        Report(dryRun + "Enumerating all webdav files...");
        var allDavItems = await dbClient.Ctx.Items.ToListAsync().ConfigureAwait(false);

        // get linked file paths
        Report(dryRun + $"Found {allDavItems.Count} webdav files.\nEnumerating all linked files...");
        var linkedIds = GetLinkedIds();
        if (linkedIds.Count < 5)
        {
            Report($"Aborted: " +
                   $"There are less than five linked files found in your library. " +
                   $"Cancelling operation to prevent accidental bulk deletion.");
            return;
        }

        // determine paths to delete
        // only delete paths that have existed longer than a day
        var minExistance = TimeSpan.FromDays(1);
        var dateThreshold = DateTime.Now.Subtract(minExistance);
        var allEmptyDirectories = allDavItems
            .Where(x => x.Type == DavItem.ItemType.Directory)
            .Where(x => x.CreatedAt < dateThreshold)
            .Where(x => x.Children.All(y => removedItems.Contains(y.Id)));
        var allUnlinkedFiles = allDavItems
            .Where(x => x.Type
                is DavItem.ItemType.NzbFile
                or DavItem.ItemType.RarFile
                or DavItem.ItemType.MultipartFile)
            .Where(x => x.CreatedAt < dateThreshold)
            .Where(x => !linkedIds.Contains(x.Id));

        // remove all empty directories
        Report(dryRun + "Removing all empty directories...");
        foreach (var emptyDirectory in allEmptyDirectories)
            RemoveItem(emptyDirectory, removedItems);

        // remove all unlinked files
        Report(dryRun + "Removing all unlinked files...");
        foreach (var unlinkedFile in allUnlinkedFiles)
            RemoveItem(unlinkedFile, removedItems);

        // return all removed paths
        _allRemovedPaths = allDavItems
            .Where(x => removedItems.Contains(x.Id))
            .Select(x => x.Path)
            .ToList();

        // delete from database in batches (files first, then directories deepest-first
        // to avoid ON DELETE CASCADE removing non-orphaned children)
        if (!isDryRun)
        {
            const int batchSize = 1000;
            var filesToDelete = allDavItems
                .Where(x => removedItems.Contains(x.Id))
                .Where(x => x.Type != DavItem.ItemType.Directory)
                .Select(x => x.Id)
                .ToList();
            var dirsToDelete = allDavItems
                .Where(x => removedItems.Contains(x.Id))
                .Where(x => x.Type == DavItem.ItemType.Directory)
                .OrderByDescending(x => x.Path.Length)
                .Select(x => x.Id)
                .ToList();
            var idsToDelete = filesToDelete.Concat(dirsToDelete).ToList();

            for (var i = 0; i < idsToDelete.Count; i += batchSize)
            {
                var batch = idsToDelete.Skip(i).Take(batchSize).ToList();
                await dbClient.Ctx.Items
                    .Where(x => batch.Contains(x.Id))
                    .ExecuteDeleteAsync()
                    .ConfigureAwait(false);

                Report($"Deleted {Math.Min(i + batchSize, idsToDelete.Count)}/{idsToDelete.Count} orphaned items...");
            }
        }

        Report(!isDryRun
            ? $"Done. Removed {_allRemovedPaths.Count} orphaned items."
            : $"Done. The task would remove {_allRemovedPaths.Count} orphaned items.");
    }

    private void RemoveItem(DavItem item, HashSet<Guid> removedItems)
    {
        // ignore protected folders
        if (item.IsProtected()) return;

        // ignore already removed items
        if (removedItems.Contains(item.Id)) return;

        // mark the item for removal
        removedItems.Add(item.Id);

        // remove the parent directory, if it is empty.
        if (item.Parent!.Children.All(x => removedItems.Contains(x.Id)))
            RemoveItem(item.Parent!, removedItems);
    }

    private HashSet<Guid> GetLinkedIds()
    {
        return OrganizedLinksUtil.GetLibraryDavItemLinks(configManager)
            .Select(x => x.DavItemId)
            .ToHashSet();
    }

    private void Report(string message)
    {
        _ = websocketManager.SendMessage(WebsocketTopic.CleanupTaskProgress, message);
    }

    public static string GetAuditReport()
    {
        return _allRemovedPaths.Count > 0
            ? string.Join("\n", _allRemovedPaths)
            : "This list is Empty.\nYou must first run the task.";
    }
}