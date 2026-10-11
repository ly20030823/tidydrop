using TidyDrop.App;
using TidyDrop.Core;

var root = Path.GetFullPath(Path.Combine(args.Length == 0 ? "artifacts/recycle-checks" : args[0], Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
await RecycleBin.RunAsync(() =>
{
    var veto = new RecycleBin.RecycleProgress();
    Assert(veto.PreDeleteItem(0, IntPtr.Zero) < 0 && veto.RefusedPermanentDelete, "permanent-delete veto");
    Assert(new RecycleBin.RecycleProgress().PreDeleteItem(0x80, IntPtr.Zero) == 0, "recycle flag accepted");
    var settings = new Settings { RootFolder = Path.Combine(root, "archive") };
    var store = new Store(Path.Combine(root, "data"));
    var catalog = new ArchiveCatalog(settings);
    var organizer = new Organizer(store, catalog);
    var history = new List<HistoryEntry>();
    var source = Path.Combine(root, "中文 name, report.txt");
    var folder = Path.Combine(root, "项目.v1");
    File.WriteAllText(source, "Recycle check 中文 bytes");
    Directory.CreateDirectory(Path.Combine(folder, "空目录"));
    File.WriteAllText(Path.Combine(folder, "报告.txt"), "nested bytes");
    Assert(organizer.Execute(organizer.Preview([source, folder], settings), history).All(result => result.Success), "fixture archive");
    var items = catalog.Load(history);
    var receipts = new List<RecycleBin.Receipt>();
    try
    {
        var removal = new ArchiveRemoval(settings, store, catalog, path =>
        {
            Assert(Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "fixture scope");
            receipts.Add(RecycleBin.Move(path));
        });
        var result = removal.Remove(items.Select(item => item.Id), history);
        Assert(result.All(item => item.Success), string.Join("; ", result.Select(item => item.Message)));
        Assert(catalog.Load(history).Count == 0 && history.All(entry => entry.State == "已移入回收站"), "catalog and history");
        Assert(receipts.Count == 2 && receipts.All(receipt => !Organizer.Exists(receipt.OriginalPath)), "native recycling");
    }
    finally
    {
        foreach (var receipt in receipts) RecycleBin.RestoreForCheck(receipt);
    }
    var fileItem = items.Single(item => !item.IsDirectory);
    var folderItem = items.Single(item => item.IsDirectory);
    Assert(File.ReadAllText(fileItem.Path) == "Recycle check 中文 bytes", "file restored bytes");
    Assert(File.ReadAllText(Path.Combine(folderItem.Path, "报告.txt")) == "nested bytes" && Directory.Exists(Path.Combine(folderItem.Path, "空目录")), "folder restored tree");
    Assert(catalog.Load(history, rebuild: true).Count == 2, "manual refresh after restoring");
    var calls = 0;
    var lockedItem = catalog.Load(history).Single(item => !item.IsDirectory);
    using (var locked = new FileStream(lockedItem.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var removal = new ArchiveRemoval(settings, store, catalog, path => { calls++; RecycleBin.Move(path); });
        Assert(!removal.Remove([lockedItem.Id], history).Single().Success && calls == 1, "locked file refusal");
    }
    Assert(File.ReadAllText(lockedItem.Path) == "Recycle check 中文 bytes" && catalog.Load(history).Count == 2, "locked file preserved");
    var html = Path.Combine(root, "page.htm");
    File.WriteAllText(html, "<html>fixture</html>");
    Directory.CreateDirectory(Path.Combine(root, "page_files"));
    var connected = Path.Combine(root, "page_files", "keep.txt");
    File.WriteAllText(connected, "unselected resource");
    var htmlReceipt = RecycleBin.Move(html);
    try { Assert(File.ReadAllText(connected) == "unselected resource", "unselected HTML companion remains untouched"); }
    finally { RecycleBin.RestoreForCheck(htmlReceipt); }
    Console.WriteLine("PASS real Windows recycle and restore for file and whole folder; catalog and history; preserved bytes and empty folders");
    return true;
});
Console.WriteLine("Fixture: " + root);
void Assert(bool value, string label) { if (!value) throw new IOException(label); }
