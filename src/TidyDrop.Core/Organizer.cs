using System.Security.Cryptography;

namespace TidyDrop.Core;

public sealed class Organizer(Store store, ArchiveCatalog? catalog = null)
{
    private static readonly Dictionary<string, string> Categories = BuildCategories();

    public IReadOnlyList<PlanItem> Preview(IEnumerable<string> paths, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RootFolder))
            throw new InvalidOperationException("请先选择归档总文件夹。");
        if (!Path.IsPathFullyQualified(settings.RootFolder))
            throw new InvalidOperationException("归档总文件夹必须是完整路径。");
        var root = Path.GetFullPath(settings.RootFolder);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<PlanItem>();
        var inputs = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var inputFolders = inputs.Select(TryDirectory).Where(path => path is not null).Cast<string>().ToArray();
        var today = DateTime.Now;
        foreach (var raw in inputs)
        {
            try
            {
                var source = Path.GetFullPath(raw);
                if (!seen.Add(source)) continue;
                var isDirectory = Directory.Exists(source);
                if (inputFolders.Any(parent => !parent.Equals(source, StringComparison.OrdinalIgnoreCase) && IsInside(source, parent)))
                {
                    plan.Add(new(source, null, "", "已包含在本次收纳的文件夹中"));
                    continue;
                }
                if (!isDirectory && !File.Exists(source))
                {
                    plan.Add(new(source, null, "", "文件不存在或无法访问"));
                    continue;
                }
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                {
                    plan.Add(new(source, null, "", "暂不处理链接或云端占位文件"));
                    continue;
                }
                if (source.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) || IsInside(source, root))
                {
                    plan.Add(new(source, null, "", "已经位于归档总文件夹中"));
                    continue;
                }
                if (isDirectory && (IsInside(root, source) || IsInside(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), source) || source.Equals(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Add(new(source, null, "", "不能收纳包含归档空间或正在运行程序的文件夹"));
                    continue;
                }
                if (isDirectory) DirectoryTransfer.Validate(source);
                var category = isDirectory ? "文件夹" : Category(source);
                var folder = Path.Combine(root, "归序", today.ToString("yyyy-MM"), today.ToString("yyyy-MM-dd"), category);
                var destination = AvailablePath(Path.Combine(folder, Path.GetFileName(source)), reserved, isDirectory);
                reserved.Add(destination);
                plan.Add(new(source, destination, category, "准备归档", isDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                plan.Add(new(raw, null, "", "无法读取：" + ex.Message));
            }
        }
        return plan;
    }

    public List<OperationResult> Execute(IEnumerable<PlanItem> plan, List<HistoryEntry> history)
    {
        var planned = plan.ToArray();
        var results = new List<OperationResult>();
        var changed = new List<HistoryEntry>();
        ArchiveCatalog.Mutation? mutation = null;
        try
        {
            if (planned.Any(item => item.CanMove)) mutation = catalog?.BeginMutation(history);
        }
        catch (Exception ex)
        {
            return planned.Select(item => new OperationResult(item.FileName, false, item.CanMove ? "材料清单无法更新，项目保持原位：" + ex.Message : item.Message)).ToList();
        }
        try
        {
            foreach (var item in planned)
            {
                if (item.Destination is null)
                {
                    if (!ExpectedSkip(item.Message))
                    {
                        history.Insert(0, new HistoryEntry { Source = item.Source, RuleName = item.RuleName, State = "未收纳", Detail = item.Message, IsDirectory = item.IsDirectory });
                        try { store.SaveHistory(history); }
                        catch (Exception ex) { results.Add(new(item.FileName, false, item.Message + "；记录保存失败：" + ex.Message)); continue; }
                    }
                    results.Add(new(item.FileName, false, item.Message));
                    continue;
                }
                var entry = new HistoryEntry
                {
                    Source = item.Source, Destination = item.Destination, RuleName = item.RuleName, IsDirectory = item.IsDirectory
                };
                changed.Add(entry);
                try
                {
                    // Persist the intent before any filesystem change. Never overwrite a destination.
                    history.Insert(0, entry);
                    store.SaveHistory(history);
                    Transfer(item.Source, item.Destination, null, hash =>
                    {
                        entry.Hash = hash;
                        store.SaveHistory(history);
                    });
                    entry.State = "已归档";
                    entry.Detail = "";
                    store.SaveHistory(history);
                    results.Add(new(item.FileName, true, "已归档"));
                }
                catch (Exception ex)
                {
                    var completed = !Exists(entry.Source) && HashMatches(entry.Destination, entry.Hash);
                    entry.State = completed ? "已归档" : "未完成";
                    entry.Detail = ex.Message;
                    try { store.SaveHistory(history); }
                    catch { /* The write-ahead record remains for recovery on next launch. */ }
                    results.Add(new(item.FileName, completed, completed ? "已归档，记录保存需检查" : "未完成：" + ex.Message));
                }
            }
        }
        finally
        {
            try { mutation?.Commit(changed); }
            catch (Exception ex)
            {
                for (var i = 0; i < results.Count; i++)
                    if (results[i].Success) results[i] = results[i] with { Message = results[i].Message + "；材料清单待修复：" + ex.Message };
            }
            finally { mutation?.Dispose(); }
        }
        return results;
    }

    public OperationResult Undo(HistoryEntry entry, List<HistoryEntry> history)
    {
        if (entry.State != "已归档") return new(entry.FileName, false, "这条记录不能撤销");
        ArchiveCatalog.Mutation? mutation = null;
        OperationResult result;
        try
        {
            if (File.Exists(entry.Source) || Directory.Exists(entry.Source))
                throw new IOException("原位置已有同名文件，撤销已停止，避免覆盖。");
            mutation = catalog?.BeginMutation(history);
            entry.State = "撤销中";
            store.SaveHistory(history);
            Transfer(entry.Destination, entry.Source, entry.Hash, _ => { });
            entry.State = "已撤销";
            entry.Detail = "";
            store.SaveHistory(history);
            result = new(entry.FileName, true, "已恢复到原位置");
        }
        catch (Exception ex)
        {
            entry.State = !Exists(entry.Destination) && HashMatches(entry.Source, entry.Hash)
                ? "已撤销" : "已归档";
            entry.Detail = ex.Message;
            try { store.SaveHistory(history); } catch { }
            result = new(entry.FileName, entry.State == "已撤销", ex.Message);
        }
        try { mutation?.Commit([entry]); }
        catch (Exception ex) { result = result with { Message = result.Message + "；材料清单待修复：" + ex.Message }; }
        finally { mutation?.Dispose(); }
        return result;
    }

    public void Recover(List<HistoryEntry> history)
    {
        var changed = false;
        foreach (var entry in history.Where(e => e.State is "待执行" or "撤销中"))
        {
            if (entry.State == "待执行")
                entry.State = !Exists(entry.Source) && HashMatches(entry.Destination, entry.Hash)
                    ? "已归档" : "未完成";
            else
                entry.State = !Exists(entry.Destination) && HashMatches(entry.Source, entry.Hash)
                    ? "已撤销" : HashMatches(entry.Destination, entry.Hash) ? "已归档" : "未完成";
            entry.Detail = "程序上次中断，已核对文件位置；未完成记录请检查两端文件。";
            changed = true;
        }
        if (changed) store.SaveHistory(history);
    }

    private static void Transfer(string source, string destination, string? expectedHash, Action<string> beforeMove)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标已有同名文件，请重新预览后整理。");
        if (Directory.Exists(source))
        {
            DirectoryTransfer.Move(source, destination, expectedHash, beforeMove);
            return;
        }
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var hash = Convert.ToHexString(SHA256.HashData(input));
        if (expectedHash is not null && !hash.Equals(expectedHash, StringComparison.Ordinal))
            throw new IOException("文件在归档后已被修改，撤销已停止。请手动确认文件内容。");
        beforeMove(hash);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        if (Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            // The open handle prevents writes while the hash and move are in progress.
            File.Move(source, destination);
            return;
        }
        // Across drives, verify the copied content before removing the original.
        var temporary = Path.Combine(parent, ".tidydrop-" + Guid.NewGuid().ToString("N") + ".tmp");
        var destinationCreated = false;
        try
        {
            input.Position = 0;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (!HashMatches(temporary, hash)) throw new IOException("复制校验失败，原文件已保留。");
            File.SetCreationTimeUtc(temporary, File.GetCreationTimeUtc(source));
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            File.Move(temporary, destination);
            destinationCreated = true;
            File.Delete(source);
        }
        catch
        {
            // If deletion fails, keep both copies and let history explain the incomplete move.
            if (!destinationCreated && File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static bool HashMatches(string path, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try
        {
            if (Directory.Exists(path)) return DirectoryTransfer.Hash(path).Equals(hash, StringComparison.Ordinal);
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static bool ExpectedSkip(string message) => message is "已包含在本次收纳的文件夹中" or "已经位于归档总文件夹中";

    private static string? TryDirectory(string raw)
    {
        try { var path = Path.GetFullPath(raw); return Directory.Exists(path) ? path : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string AvailablePath(string path, HashSet<string> reserved, bool isDirectory)
    {
        var candidate = path;
        for (var i = 1; File.Exists(candidate) || Directory.Exists(candidate) || reserved.Contains(candidate); i++)
            candidate = Path.Combine(Path.GetDirectoryName(path)!, isDirectory ? $"{Path.GetFileName(path)} ({i})" : $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
        return candidate;
    }

    private static bool IsInside(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Category(string path) => Categories.GetValueOrDefault(Path.GetExtension(path).ToLowerInvariant(), "其他");

    private static Dictionary<string, string> BuildCategories()
    {
        var map = new Dictionary<string, string>();
        Add("文档", ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .rtf .csv .tsv .odt .ods .odp .epub .pages .numbers .key");
        Add("图片", ".jpg .jpeg .png .gif .bmp .webp .svg .ico .tif .tiff .heic .heif .avif .raw .cr2 .nef");
        Add("视频", ".mp4 .mkv .mov .avi .wmv .flv .webm .m4v .mpeg .mpg .3gp");
        Add("音频", ".mp3 .wav .flac .aac .m4a .ogg .wma .aiff .opus");
        Add("压缩包", ".zip .rar .7z .tar .gz .bz2 .xz .iso");
        Add("安装包", ".exe .msi .msix .appx .apk .msixbundle .appxbundle");
        Add("设计素材", ".psd .ai .eps .sketch .fig .xd .blend .fbx .obj .dwg .dxf .stl");
        Add("代码与数据", ".py .js .ts .jsx .tsx .html .css .json .xml .yaml .yml .sql .cs .cpp .c .h .java .go .rs .ipynb .toml");
        Add("快捷方式", ".lnk .url");
        return map;
        void Add(string category, string extensions)
        {
            foreach (var extension in extensions.Split(' ')) map.Add(extension, category);
        }
    }
}
