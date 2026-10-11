namespace TidyDrop.Core;

// The platform callback must recycle the item or throw; permanent deletion is never a fallback.
public sealed class ArchiveRemoval(Settings settings, Store store, ArchiveCatalog catalog, Action<string> recycle)
{
    public List<OperationResult> Remove(IEnumerable<string> ids, List<HistoryEntry> history)
    {
        using var mutation = catalog.BeginMutation(history);
        var known = mutation.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var removed = new List<ArchiveItem>();
        var results = new List<OperationResult>();
        var requiresRepair = false;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!known.TryGetValue(id, out var item))
            {
                results.Add(new("材料", false, "材料清单已变化，请刷新后重试。"));
                continue;
            }
            var records = history.Where(entry => entry.State == "已归档" && !string.IsNullOrWhiteSpace(entry.Destination) && Path.GetFullPath(entry.Destination).Equals(item.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            var started = false;
            try
            {
                Validate(item, records.Length > 0);
                foreach (var entry in records) entry.State = "回收中";
                started = true;
                store.SaveHistory(history); // Stop before touching materials if the journal cannot be written.
                recycle(item.Path);
                if (Organizer.Exists(item.Path)) throw new IOException("材料仍在原位置，未移入回收站。");
                removed.Add(item);
                foreach (var entry in records) { entry.State = "已移入回收站"; entry.Detail = "如需恢复，请在 Windows 回收站还原，再点击归序的「刷新」。"; }
                var detail = "已移入回收站";
                try { store.SaveHistory(history); }
                catch (Exception ex) { detail += "；记录保存需检查：" + ex.Message; }
                results.Add(new(item.FileName, true, detail));
            }
            catch (Exception ex)
            {
                if (started)
                {
                    var exists = Organizer.Exists(item.Path);
                    if (!exists) requiresRepair = true;
                    foreach (var entry in records) { entry.State = exists ? "已归档" : "回收待核对"; entry.Detail = ex.Message; }
                    try { store.SaveHistory(history); } catch { /* The write-ahead state is repaired on launch. */ }
                }
                results.Add(new(item.FileName, false, ex.Message));
            }
        }
        // If Windows moved an item but failed to return a receipt, reconcile the pending operation on the next read.
        try { if (!requiresRepair) mutation.CommitRemoval(removed); }
        catch (Exception ex)
        {
            for (var i = 0; i < results.Count; i++)
                results[i] = results[i] with { Message = results[i].Message + "；材料清单待修复：" + ex.Message };
        }
        return results;
    }

    private void Validate(ArchiveItem item, bool legacyRecorded)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.RootFolder));
        var path = Path.GetFullPath(item.Path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.Equals(Path.Combine(root, "归序"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("只能删除归档空间中的材料。");
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var managed = parts.Length == 5 && parts[0] == "归序" && parts[1] == item.Date[..7] && parts[2] == item.Date && parts[3] == item.Category;
        if (!managed && !legacyRecorded) throw new IOException("这不是清单中可确认的归档材料，请刷新后重试。");
        if (!Organizer.Exists(path)) throw new IOException("这份材料已被移走，请点击「刷新」。");
        if (Directory.Exists(path) != item.IsDirectory) throw new IOException("材料类型已变化，请刷新后重试。");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("暂不删除链接或云端占位项，请先确认实际材料的位置。");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (item.IsDirectory) DirectoryTransfer.Validate(path);
    }
}
