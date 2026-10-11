using TidyDrop.Core;

var checksRoot = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/checks");
var runRoot = Path.Combine(checksRoot, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
var passed = 0;
var failed = 0;
Check("Preview has no side effects; intake date, case-insensitive categories, and fallback", context =>
{
    var doc = context.File("报告.PDF", "document");
    var photo = context.File("照片.png", "image");
    var other = context.File("未知.xyz", "other");
    File.SetLastWriteTime(doc, new DateTime(2025, 3, 12));
    context.Settings.DateFolders = true;
    var plan = context.Engine.Preview([doc, photo, other], context.Settings);
    Assert(plan[0].Destination == Path.Combine(context.Settings.RootFolder, "归序", DateTime.Now.ToString("yyyy-MM"), DateTime.Now.ToString("yyyy-MM-dd"), "文档", "报告.PDF"));
    Assert(plan[1].RuleName == "图片" && plan[2].RuleName == "其他");
    Assert(File.Exists(doc) && !Directory.Exists(context.Settings.RootFolder));
});
Check("Move, persistent history, and undo preserve the original bytes", context =>
{
    var source = context.File("a.txt", "hello 中文");
    var plan = context.Engine.Preview([source], context.Settings);
    Assert(context.Engine.Execute(plan, context.History).Single().Success);
    Assert(!File.Exists(source) && File.ReadAllText(plan[0].Destination!) == "hello 中文");
    var loaded = context.Store.LoadHistory();
    Assert(loaded.Count == 1 && loaded[0].State == "已归档" && loaded[0].Hash.Length == 64);
    Assert(context.Engine.Undo(loaded[0], loaded).Success);
    Assert(File.ReadAllText(source) == "hello 中文" && !File.Exists(plan[0].Destination));
    Assert(context.Store.LoadHistory()[0].State == "已撤销");
});
Check("Existing names and duplicate names within a batch are never overwritten", context =>
{
    var targetFolder = Path.Combine(context.Settings.RootFolder, "归序", DateTime.Now.ToString("yyyy-MM"), DateTime.Now.ToString("yyyy-MM-dd"), "文档");
    Directory.CreateDirectory(targetFolder);
    var existing = Path.Combine(targetFolder, "a.txt");
    File.WriteAllText(existing, "keep");
    var first = context.File("one/a.txt", "first");
    var second = context.File("two/a.txt", "second");
    var plan = context.Engine.Preview([first, second, first], context.Settings);
    Assert(plan.Count == 2 && plan[0].Destination!.EndsWith("a (1).txt") && plan[1].Destination!.EndsWith("a (2).txt"));
    Assert(context.Engine.Execute(plan, context.History).All(r => r.Success));
    Assert(File.ReadAllText(existing) == "keep");
});
Check("A target created after preview is preserved and the source stays put", context =>
{
    var source = context.File("a.txt", "original");
    var plan = context.Engine.Preview([source], context.Settings);
    Directory.CreateDirectory(Path.GetDirectoryName(plan[0].Destination!)!);
    File.WriteAllText(plan[0].Destination!, "new occupant");
    Assert(!context.Engine.Execute(plan, context.History)[0].Success);
    Assert(File.ReadAllText(source) == "original" && File.ReadAllText(plan[0].Destination!) == "new occupant");
});
Check("Undo refuses a modified document", context =>
{
    var source = context.File("a.txt", "original");
    context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
    File.WriteAllText(context.History[0].Destination, "edited later");
    Assert(!context.Engine.Undo(context.History[0], context.History).Success);
    Assert(!File.Exists(source) && File.ReadAllText(context.History[0].Destination) == "edited later");
});
Check("Undo refuses to overwrite a new file at the original path", context =>
{
    var source = context.File("a.txt", "original");
    context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
    File.WriteAllText(source, "new occupant");
    Assert(!context.Engine.Undo(context.History[0], context.History).Success);
    Assert(File.ReadAllText(source) == "new occupant" && File.Exists(context.History[0].Destination));
});
Check("Archive ancestors, missing items, and items already in the archive are skipped", context =>
{
    var inside = Path.Combine(context.Settings.RootFolder, "a.txt");
    Directory.CreateDirectory(context.Settings.RootFolder);
    File.WriteAllText(inside, "inside");
    var plan = context.Engine.Preview([context.Folder, Path.Combine(context.Folder, "missing.txt"), inside], context.Settings);
    Assert(plan.Count == 3 && plan.All(p => !p.CanMove));
});
Check("Folders preserve nested contents and empty directories; overlapping inputs are skipped; undo verifies the tree", context =>
{
    var source = Path.Combine(context.Folder, "项目.v1");
    Directory.CreateDirectory(Path.Combine(source, "空目录"));
    var child = context.File("项目.v1/子目录/报告.txt", "nested 中文");
    var plan = context.Engine.Preview([child, source], context.Settings);
    Assert(!plan[0].CanMove && plan[1].IsDirectory && plan[1].RuleName == "文件夹");
    var results = context.Engine.Execute(plan, context.History);
    Assert(!results[0].Success && results[1].Success && !Directory.Exists(source));
    var destination = context.History[0].Destination;
    Assert(Directory.Exists(Path.Combine(destination, "空目录")) && File.ReadAllText(Path.Combine(destination, "子目录", "报告.txt")) == "nested 中文");
    Assert(context.Engine.Undo(context.History[0], context.History).Success);
    Assert(File.ReadAllText(child) == "nested 中文" && Directory.Exists(Path.Combine(source, "空目录")));
});
Check("Folder conflicts retain dotted names, and added children prevent unsafe undo", context =>
{
    var source = Path.Combine(context.Folder, "项目.v1");
    Directory.CreateDirectory(source);
    var first = context.Engine.Preview([source], context.Settings)[0];
    Directory.CreateDirectory(first.Destination!);
    var second = context.Engine.Preview([source], context.Settings)[0];
    Assert(second.Destination!.EndsWith("项目.v1 (1)"));
    Assert(context.Engine.Execute([second], context.History)[0].Success);
    File.WriteAllText(Path.Combine(second.Destination!, "新文件.txt"), "new");
    Assert(!context.Engine.Undo(context.History[0], context.History).Success);
    Assert(!Directory.Exists(source) && File.Exists(Path.Combine(second.Destination!, "新文件.txt")));
});
Check("Library rebuilds from storage, shows actual intake day, and treats a folder as one item", context =>
{
    var doc = context.File("报告.txt", "doc");
    var folder = Path.Combine(context.Folder, "项目");
    Directory.CreateDirectory(folder);
    context.File("项目/内部.txt", "inside");
    context.Engine.Execute(context.Engine.Preview([doc, folder], context.Settings), context.History);
    var library = ArchiveLibrary.Read(context.Settings, []);
    Assert(library.Count == 2 && library.Any(x => x.IsDirectory && x.Category == "文件夹"));
    Assert(library.All(x => x.Date == DateTime.Now.ToString("yyyy-MM-dd")));
    context.Engine.Undo(context.History[0], context.History);
    Assert(ArchiveLibrary.Read(context.Settings, context.History).Count == 1);
    var legacy = Path.Combine(context.Settings.RootFolder, "图片", "旧图.png");
    Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
    File.WriteAllText(legacy, "old");
    context.History.Add(new HistoryEntry { Destination = legacy, State = "已归档", RuleName = "图片", Time = new DateTimeOffset(2025, 3, 12, 10, 0, 0, TimeSpan.FromHours(8)) });
    Assert(ArchiveLibrary.Read(context.Settings, context.History).Any(x => x.Path == legacy && x.Date == "2025-03-12"));
});
Check("A locked file inside a folder stops the whole folder before moving", context =>
{
    var source = Path.Combine(context.Folder, "项目");
    var child = context.File("项目/locked.txt", "keep");
    var plan = context.Engine.Preview([source], context.Settings);
    using (var locked = new FileStream(child, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Assert(!context.Engine.Execute(plan, context.History)[0].Success);
    Assert(File.ReadAllText(child) == "keep" && !Directory.Exists(plan[0].Destination));
});
Check("Catalog starts from saved metadata; external changes appear only after manual rebuild", context =>
{
    var source = context.File("已收纳.txt", "keep");
    Assert(context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History)[0].Success);
    var destination = context.History[0].Destination;
    var outsideChange = Path.Combine(Path.GetDirectoryName(destination)!, "手动添加.txt");
    File.WriteAllText(outsideChange, "external");
    File.Delete(destination);
    var reopened = new ArchiveCatalog(context.Settings);
    var cached = reopened.Load(context.History);
    Assert(cached.Count == 1 && cached[0].Path == destination);
    var rebuilt = reopened.Load(context.History, rebuild: true);
    Assert(rebuilt.Count == 1 && rebuilt[0].Path == outsideChange);
    Assert(new ArchiveCatalog(context.Settings).Load([]).Single().Path == outsideChange);
});
Check("Collection and undo update the catalog incrementally for files and whole folders", context =>
{
    var first = context.File("报告.txt", "doc");
    var folder = Path.Combine(context.Folder, "项目");
    context.File("项目/内部.txt", "inside");
    Directory.CreateDirectory(Path.Combine(folder, "空目录"));
    Assert(context.Engine.Execute(context.Engine.Preview([first, folder], context.Settings), context.History).All(result => result.Success));
    Assert(context.Catalog.Load([]).Count == 2 && context.Catalog.Load([]).Count(item => item.IsDirectory) == 1);
    var external = Path.Combine(Path.GetDirectoryName(context.History.Single(entry => !entry.IsDirectory).Destination)!, "未核对.txt");
    File.WriteAllText(external, "external");
    var next = context.File("新增.txt", "new");
    Assert(context.Engine.Execute(context.Engine.Preview([next], context.Settings), context.History)[0].Success);
    Assert(new ArchiveCatalog(context.Settings).Load([]).Count == 3);
    Assert(context.Engine.Undo(context.History.Single(entry => entry.IsDirectory), context.History).Success);
    Assert(context.Catalog.Load([]).Count == 2 && File.ReadAllText(Path.Combine(folder, "内部.txt")) == "inside");
    Assert(context.Catalog.Load(context.History, rebuild: true).Count == 3);
});
Check("First catalog migration keeps legacy items and ignores metadata and temporary files", context =>
{
    var managed = Path.Combine(context.Settings.RootFolder, "归序", "2026-09", "2026-09-30", "文档", "旧材料.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
    File.WriteAllText(managed, "managed");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(managed)!, ".tidydrop-partial.tmp"), "temporary");
    var legacy = Path.Combine(context.Settings.RootFolder, "图片", "旧图.png");
    Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
    File.WriteAllText(legacy, "old");
    context.History.Add(new HistoryEntry { Destination = legacy, State = "已归档", RuleName = "图片", Time = new DateTimeOffset(2025, 3, 12, 10, 0, 0, TimeSpan.FromHours(8)) });
    var migrated = context.Catalog.Load(context.History);
    Assert(migrated.Count == 2 && migrated.Any(item => item.Path == legacy && item.Date == "2025-03-12"));
    Assert(File.Exists(CatalogPath(context)) && new ArchiveCatalog(context.Settings).Load([]).Count == 2);
    Assert(ArchiveLibrary.Read(context.Settings, context.History).Count == 2);
});
Check("Relative catalog paths survive moving the complete archive directory", context =>
{
    var source = context.File("迁移.txt", "keep");
    context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
    var relative = Path.GetRelativePath(context.Settings.RootFolder, context.History[0].Destination);
    var target = Path.Combine(context.Folder, "迁移后的归档");
    var fixturePrefix = Path.GetFullPath(context.Folder) + Path.DirectorySeparatorChar;
    Assert(Path.GetFullPath(context.Settings.RootFolder).StartsWith(fixturePrefix) && Path.GetFullPath(target).StartsWith(fixturePrefix));
    Directory.Move(context.Settings.RootFolder, target);
    context.Settings.RootFolder = target;
    var catalog = new ArchiveCatalog(context.Settings).Load([]);
    Assert(catalog.Count == 1 && catalog[0].Path == Path.Combine(target, relative));
    Assert(File.ReadAllText(catalog[0].Path) == "keep");
});
Check("Invalid catalogs are backed up and rebuilt; escaping paths cannot enter the catalog", context =>
{
    var source = context.File("安全.txt", "safe");
    context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
    File.WriteAllText(CatalogPath(context), "broken JSON");
    Assert(new ArchiveCatalog(context.Settings).Load(context.History).Count == 1);
    var metadata = Path.GetDirectoryName(CatalogPath(context))!;
    Assert(Directory.GetFiles(metadata, "catalog.invalid-*.json").Any(path => File.ReadAllText(path) == "broken JSON"));
    File.WriteAllText(CatalogPath(context), "{\"version\":1,\"items\":[{\"id\":\"bad\",\"relativePath\":\"../outside.txt\",\"category\":\"文档\",\"date\":\"2026-10-11\",\"isDirectory\":false}]}");
    var recovered = new ArchiveCatalog(context.Settings).Load(context.History);
    Assert(recovered.Count == 1 && recovered[0].FileName == "安全.txt");
});
Check("Interrupted catalog updates rebuild once and include material already moved", context =>
{
    var source = context.File("第一份.txt", "first");
    context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
    var moved = Path.Combine(Path.GetDirectoryName(context.History[0].Destination)!, "中断后.txt");
    File.WriteAllText(moved, "moved before catalog commit");
    File.WriteAllText(PendingPath(context), "interrupted");
    Assert(new ArchiveCatalog(context.Settings).Load(context.History).Count == 2);
    Assert(!File.Exists(PendingPath(context)));
    var laterExternal = Path.Combine(Path.GetDirectoryName(moved)!, "暂未更新.txt");
    File.WriteAllText(laterExternal, "external");
    Assert(new ArchiveCatalog(context.Settings).Load(context.History).Count == 2);
});
Check("Catalog failure before collection leaves the source in place", context =>
{
    Directory.CreateDirectory(context.Settings.RootFolder);
    context.Catalog.Load([]);
    var source = context.File("不能更新.txt", "keep");
    var pending = PendingPath(context);
    File.WriteAllText(pending, "blocked");
    using (var locked = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.None))
        Assert(!context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History)[0].Success);
    Assert(File.ReadAllText(source) == "keep" && context.History.Count == 0);
});
Check("Catalog failure after collection keeps the material and a durable repair marker", context =>
{
    Directory.CreateDirectory(context.Settings.RootFolder);
    context.Catalog.Load([]);
    var source = context.File("已移动.txt", "keep");
    using (var locked = new FileStream(CatalogPath(context), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var result = context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History)[0];
        Assert(result.Success && result.Message.Contains("清单待修复"));
        Assert(!File.Exists(source) && File.ReadAllText(context.History[0].Destination) == "keep" && File.Exists(PendingPath(context)));
    }
    Assert(new ArchiveCatalog(context.Settings).Load(context.History).Count == 1 && !File.Exists(PendingPath(context)));
});
Check("Two writers to the same archive preserve both catalog additions", context =>
{
    var first = context.File("one.txt", "first");
    var second = context.File("two.txt", "second");
    var otherHistory = new List<HistoryEntry>();
    var other = new Organizer(new Store(Path.Combine(context.Folder, "other-data")), new ArchiveCatalog(context.Settings));
    var firstPlan = context.Engine.Preview([first], context.Settings);
    var secondPlan = other.Preview([second], context.Settings);
    var tasks = new[] {
        Task.Run(() => context.Engine.Execute(firstPlan, context.History)),
        Task.Run(() => other.Execute(secondPlan, otherHistory))
    };
    Task.WaitAll(tasks);
    Assert(tasks.All(task => task.Result.Single().Success));
    Assert(new ArchiveCatalog(context.Settings).Load([]).Count == 2 && !File.Exists(PendingPath(context)));
});
Check("Catalog failure after undo leaves restored bytes intact and repairs the removal", context =>
{
    var source = context.File("撤销.txt", "keep");
    context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
    var destination = context.History[0].Destination;
    using (var locked = new FileStream(CatalogPath(context), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var result = context.Engine.Undo(context.History[0], context.History);
        Assert(result.Success && result.Message.Contains("清单待修复"));
        Assert(File.ReadAllText(source) == "keep" && !File.Exists(destination) && File.Exists(PendingPath(context)));
    }
    Assert(new ArchiveCatalog(context.Settings).Load(context.History).Count == 0 && !File.Exists(PendingPath(context)));
});
Check("An exclusively locked source remains untouched", context =>
{
    var source = context.File("a.txt", "locked");
    var plan = context.Engine.Preview([source], context.Settings);
    using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Assert(!context.Engine.Execute(plan, context.History)[0].Success);
    Assert(File.ReadAllText(source) == "locked");
});
Check("Missing and invalid inputs cannot prevent valid items in the same batch", context =>
{
    var valid = context.File("valid.txt", "keep");
    var plan = context.Engine.Preview(["bad\0path", Path.Combine(context.Folder, "missing.txt"), valid], context.Settings);
    Assert(plan.Count == 3 && !plan[0].CanMove && !plan[1].CanMove && plan[2].CanMove);
    // A missing but syntactically valid input is retained as a searchable failure record.
    var results = context.Engine.Execute([plan[1], plan[2]], context.History);
    Assert(!results[0].Success && results[1].Success && context.History.Any(e => e.State == "未收纳"));
});
Check("Journal failure stops before moving any files", context =>
{
    var source = context.File("a.txt", "original");
    var blocker = context.File("not-a-directory", "block");
    var engine = new Organizer(new Store(blocker));
    Assert(!engine.Execute(engine.Preview([source], context.Settings), context.History)[0].Success);
    Assert(File.ReadAllText(source) == "original" && !Directory.Exists(context.Settings.RootFolder));
});
Check("Recovery identifies completed moves and interrupted undo", context =>
{
    var source = context.File("a.txt", "original");
    context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
    context.History[0].State = "待执行";
    context.Engine.Recover(context.History);
    Assert(context.History[0].State == "已归档");
    context.Engine.Undo(context.History[0], context.History);
    context.History[0].State = "撤销中";
    context.Engine.Recover(context.History);
    Assert(context.History[0].State == "已撤销" && File.Exists(source));
});
Check("Settings round-trip and corrupt JSON is reported without replacement", context =>
{
    context.Settings.DateFolders = true;
    context.Settings.AutoOrganize = false;
    context.Store.SaveSettings(context.Settings);
    var loaded = context.Store.LoadSettings();
    Assert(loaded.RootFolder == context.Settings.RootFolder && loaded.DateFolders && !loaded.AutoOrganize);
    var corrupt = Path.Combine(context.Folder, "data", "history.json");
    File.WriteAllText(corrupt, "broken JSON");
    var threw = false;
    try { context.Store.LoadHistory(); } catch { threw = true; }
    Assert(threw && File.ReadAllText(corrupt) == "broken JSON");
});
if (args.Length > 1)
{
    Check("Cross-drive transfer verifies bytes and supports undo", context =>
    {
        context.Settings.RootFolder = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
        Assert(!Path.GetPathRoot(context.Folder)!.Equals(Path.GetPathRoot(context.Settings.RootFolder), StringComparison.OrdinalIgnoreCase));
        var source = context.File("跨盘.txt", string.Concat(Enumerable.Repeat("test 中文\n", 10000)));
        var original = File.ReadAllText(source);
        var modified = new DateTime(2025, 4, 3, 12, 0, 0);
        File.SetLastWriteTime(source, modified);
        var results = context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History);
        Assert(results[0].Success && !File.Exists(source));
        Assert(File.ReadAllText(context.History[0].Destination) == original);
        Assert(File.GetLastWriteTime(context.History[0].Destination) == modified);
        Assert(context.Engine.Undo(context.History[0], context.History).Success && File.ReadAllText(source) == original);
    });
    Check("Cross-drive folders preserve nested bytes, empty folders, timestamps, and support undo", context =>
    {
        context.Settings.RootFolder = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(context.Folder, "项目.v1");
        var child = context.File("项目.v1/a/跨盘.txt", "cross folder 中文");
        Directory.CreateDirectory(Path.Combine(source, "空目录"));
        var modified = new DateTime(2025, 4, 3, 12, 0, 0);
        File.SetLastWriteTime(child, modified);
        Assert(context.Engine.Execute(context.Engine.Preview([source], context.Settings), context.History)[0].Success);
        var destination = context.History[0].Destination;
        Assert(!Directory.Exists(source) && File.ReadAllText(Path.Combine(destination, "a", "跨盘.txt")) == "cross folder 中文");
        Assert(Directory.Exists(Path.Combine(destination, "空目录")) && File.GetLastWriteTime(Path.Combine(destination, "a", "跨盘.txt")) == modified);
        Assert(context.Engine.Undo(context.History[0], context.History).Success && File.Exists(child));
    });
}
Console.WriteLine($"{passed} passed, {failed} failed. Test data: {runRoot}");
return failed == 0 ? 0 : 1;

void Check(string name, Action<Context> test)
{
    var folder = Path.Combine(runRoot, (passed + failed + 1).ToString("00"));
    Directory.CreateDirectory(folder);
    try { test(new Context(folder)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
string CatalogPath(Context context) => Path.Combine(context.Settings.RootFolder, "归序", ArchiveCatalog.MetadataFolder, ArchiveCatalog.CatalogFile);
string PendingPath(Context context) => Path.Combine(context.Settings.RootFolder, "归序", ArchiveCatalog.MetadataFolder, ArchiveCatalog.PendingFile);

sealed class Context
{
    public string Folder { get; }
    public Store Store { get; }
    public Organizer Engine { get; }
    public ArchiveCatalog Catalog { get; }
    public Settings Settings { get; }
    public List<HistoryEntry> History { get; } = [];
    public Context(string folder)
    {
        Folder = folder;
        Store = new Store(Path.Combine(folder, "data"));
        Settings = new Settings { RootFolder = Path.Combine(folder, "archive") };
        Catalog = new ArchiveCatalog(Settings);
        Engine = new Organizer(Store, Catalog);
    }
    public string File(string relative, string content)
    {
        var path = Path.Combine(Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }
}
