using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TidyDrop.Core;

public sealed class ArchiveCatalog(Settings settings)
{
    public const string MetadataFolder = ".tidydrop";
    public const string CatalogFile = "catalog.json";
    public const string PendingFile = "pending";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    // Reading an established catalog does not enumerate or check each material on disk.
    public List<ArchiveItem> Load(IEnumerable<HistoryEntry> history, bool rebuild = false)
    {
        if (string.IsNullOrWhiteSpace(settings.RootFolder)) return [];
        var root = Path.GetFullPath(settings.RootFolder);
        using var guard = Acquire(root);
        if (!Directory.Exists(root)) return [];
        var document = rebuild ? Rebuild(root, history) : LoadDocument(root, history);
        return ToItems(root, document);
    }

    public Mutation BeginMutation(IEnumerable<HistoryEntry> history)
    {
        if (string.IsNullOrWhiteSpace(settings.RootFolder)) throw new IOException("请先选择归档总文件夹。");
        var root = Path.GetFullPath(settings.RootFolder);
        var guard = Acquire(root);
        try
        {
            Directory.CreateDirectory(root);
            var document = LoadDocument(root, history);
            EnsureMetadata(root);
            WriteAtomically(Path.Combine(Metadata(root), PendingFile), Encoding.UTF8.GetBytes("Material operation in progress"));
            return new Mutation(root, document, guard);
        }
        catch { guard.Dispose(); throw; }
    }

    private static CatalogDocument LoadDocument(string root, IEnumerable<HistoryEntry> history)
    {
        CheckMetadata(root);
        var path = Path.Combine(Metadata(root), CatalogFile);
        if (!File.Exists(path) || File.Exists(Path.Combine(Metadata(root), PendingFile))) return Rebuild(root, history);
        try
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.Version != 1 || document.Items is null) throw new InvalidDataException("材料清单格式不受支持。");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Category)
                    || !DateTime.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new InvalidDataException("材料清单包含无效项目。");
                var fullPath = Resolve(root, item.RelativePath);
                if (!paths.Add(fullPath)) throw new InvalidDataException("材料清单包含重复路径。");
            }
            return document;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Preserve the invalid metadata before rebuilding; materials are never replaced here.
            File.Copy(path, Path.Combine(Metadata(root), "catalog.invalid-" + Guid.NewGuid().ToString("N") + ".json"));
            return Rebuild(root, history);
        }
    }

    private static CatalogDocument Rebuild(string root, IEnumerable<HistoryEntry> history)
    {
        var items = ArchiveLibrary.Read(new Settings { RootFolder = root }, history);
        var document = new CatalogDocument { Items = items.Select(item => new CatalogEntry(item.Id, Path.GetRelativePath(root, item.Path), item.Category, item.Date, item.IsDirectory)).ToList() };
        Save(root, document);
        ClearPending(root);
        return document;
    }

    private static List<ArchiveItem> ToItems(string root, CatalogDocument document) => document.Items
        .Select(item => { var path = Resolve(root, item.RelativePath); return new ArchiveItem(item.Id, Path.GetFileName(path), path, item.Category, item.Date, item.IsDirectory); })
        .OrderByDescending(item => item.Date).ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("材料清单路径必须相对于归档总文件夹。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var metadataPrefix = Metadata(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || full.Equals(Metadata(root), StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(metadataPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("材料清单路径超出材料范围。");
        return full;
    }

    private static string Metadata(string root) => Path.Combine(root, "归序", MetadataFolder);

    private static void CheckMetadata(string root)
    {
        foreach (var directory in new[] { Path.Combine(root, "归序"), Metadata(root) })
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("归序的管理目录不能是链接或云端占位目录。");
    }

    private static void EnsureMetadata(string root) { CheckMetadata(root); Directory.CreateDirectory(Metadata(root)); }

    private static void Save(string root, CatalogDocument document)
    {
        EnsureMetadata(root);
        document.UpdatedAt = DateTimeOffset.Now;
        WriteAtomically(Path.Combine(Metadata(root), CatalogFile), JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Keep the last valid catalog if cleanup is unavailable. */ }
        }
    }

    private static void ClearPending(string root)
    {
        var path = Path.Combine(Metadata(root), PendingFile);
        if (File.Exists(path)) File.Delete(path);
    }

    private static IDisposable Acquire(string root)
    {
        var name = "Local\\TidyDrop-Catalog-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(root).ToUpperInvariant())))[..24];
        var mutex = new Mutex(false, name);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("其他归序窗口正在更新材料清单，请稍后重试。"); }
            catch (AbandonedMutexException) { /* The pending marker repairs interrupted writers. */ }
            return new Guard(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class Guard(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    internal sealed class CatalogDocument
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset UpdatedAt { get; set; }
        public List<CatalogEntry> Items { get; set; } = [];
    }

    internal sealed record CatalogEntry(string Id, string RelativePath, string Category, string Date, bool IsDirectory);

    public sealed class Mutation : IDisposable
    {
        private readonly string root;
        private readonly CatalogDocument document;
        private readonly IDisposable guard;
        private bool disposed;

        internal Mutation(string root, CatalogDocument document, IDisposable guard) { this.root = root; this.document = document; this.guard = guard; }

        public void Commit(IEnumerable<HistoryEntry> changed)
        {
            var entries = document.Items.ToDictionary(item => Resolve(root, item.RelativePath), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in changed)
            {
                var path = Path.GetFullPath(entry.Destination);
                var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Organizer.Exists(path)) { entries.Remove(path); continue; }
                var relative = Path.GetRelativePath(root, path);
                Resolve(root, relative);
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var managed = parts.Length == 5 && parts[0] == "归序" && DateTime.TryParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                entries[path] = new(entries.GetValueOrDefault(path)?.Id ?? entry.Id, relative, managed ? parts[3] : entry.RuleName,
                    managed ? parts[2] : entry.Time.ToLocalTime().ToString("yyyy-MM-dd"), Directory.Exists(path));
            }
            document.Items = entries.Values.ToList();
            Save(root, document);
            ClearPending(root);
        }

        // An unsuccessful commit intentionally leaves the durable pending marker behind.
        public void Dispose() { if (!disposed) { disposed = true; guard.Dispose(); } }
    }
}
