using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TidyDrop.App;

// Independent strategies: ShellNew keys are atomically renamed, SendTo files are hidden.
// Never copy/delete a template tree or remove a user's shortcut to disable a menu.
internal sealed class SpecialMenus(string journalPath = @"Software\TidyDrop\SpecialMenuOverrides", string? sendToPath = null, string? onlyExtension = null)
{
    private const string DisabledNew = "TidyDrop.DisabledShellNew";
    private sealed record Target(MenuEntry Entry, string Path, string Hash, RegistryHive Hive = RegistryHive.CurrentUser);
    private readonly Dictionary<string, Target> targets = [];
    private string SendTo => sendToPath ?? Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    public List<MenuEntry> Scan()
    {
        targets.Clear();
        var result = new List<MenuEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            if (onlyExtension is not null && hive == RegistryHive.LocalMachine) continue;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var classes = root.OpenSubKey(@"Software\Classes");
            foreach (var extension in onlyExtension is not null ? [onlyExtension] : (classes?.GetSubKeyNames() ?? []).Where(n => n.StartsWith('.')))
            {
                try
                {
                    using var key = classes?.OpenSubKey(extension);
                    if (key is null) continue;
                    ReadNew(root, hive, extension, extension, result);
                    foreach (var child in key.GetSubKeyNames().Where(n => n is not ("ShellNew" or DisabledNew or "-ShellNew")))
                        ReadNew(root, hive, extension + "\\" + child, extension, result);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        if (Directory.Exists(SendTo))
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(SendTo))
            {
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var attrs = File.GetAttributes(file);
                    var eligible = (attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && new FileInfo(file).Length <= 8 * 1024 * 1024;
                    var hash = eligible ? FileHash(file) : "";
                    var id = "special:" + MenuRegistry.Id("sendto:" + file);
                    var managed = Owned(id, file);
                    var enabled = (attrs & FileAttributes.Hidden) == 0;
                    var entry = new MenuEntry(id, Path.GetFileNameWithoutExtension(file), "当前账户", "发送到", "发送到项目", enabled, eligible && (enabled || managed), managed,
                        !eligible ? "目录、链接目录或较大的项目仅查看。" : !enabled && !managed ? "此项目已被其他工具隐藏，请在原工具中恢复。" : "只隐藏当前账户的发送到入口，保留快捷方式及其目标。",
                        id, file, "sendto", RawName: Path.GetFileName(file), Registration: file, State: enabled ? "已启用" : "已停用");
                    result.Add(entry); targets[id] = new(entry, file, hash);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return result;
    }

    private void ReadNew(RegistryKey root, RegistryHive hive, string parent, string extension, List<MenuEntry> result)
    {
        var path = @"Software\Classes\" + parent + @"\ShellNew";
        using var key = root.OpenSubKey(path);
        using var hidden = root.OpenSubKey(@"Software\Classes\" + parent + "\\" + DisabledNew);
        using var foreign = root.OpenSubKey(@"Software\Classes\" + parent + @"\-ShellNew");
        if (key is null && hidden is null && foreign is null) return;
        var template = key ?? hidden ?? foreign!;
        var id = "special:" + MenuRegistry.Id("new:" + hive + ":" + path);
        var managed = Owned(id, path);
        using var merged = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        using var association = merged.OpenSubKey(extension);
        var progid = association?.GetValue("") as string ?? "";
        using var type = progid.Length > 0 ? merged.OpenSubKey(progid) : null;
        var raw = template.GetValue("MenuText") as string ?? type?.GetValue("FriendlyTypeName") as string ?? type?.GetValue("") as string ?? extension;
        var title = MenuRegistry.ResolveLabel(raw, extension.ToUpperInvariant() + " 文件");
        var viable = new[] { "NullFile", "Data", "FileName", "Directory", "Command" }.Any(n => template.GetValueNames().Contains(n, StringComparer.OrdinalIgnoreCase));
        var conflict = key is not null && (hidden is not null || foreign is not null);
        var enabled = key is not null;
        var editable = viable && type is not null && extension != ".lnk" && !conflict && (enabled || managed);
        var machine = hive == RegistryHive.LocalMachine;
        var detail = conflict ? "原位置和归序备份位置同时存在，未覆盖软件更新。"
            : foreign is not null && key is null && hidden is null ? "已被其他工具移动隐藏，请在原工具中恢复。"
            : !viable || type is null ? "模板或文件关联不完整，当前仅查看。"
            : hidden is not null && !managed ? "存在未归归序管理的备份，当前仅查看。"
            : machine ? "整台电脑的新建模板；调整需要管理员确认，影响所有账户。只重命名模板注册，保留所有数据。"
            : "当前账户的新建模板；只重命名模板注册，保留所有数据。";
        using var otherRoot = RegistryKey.OpenBaseKey(machine ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        using var otherTemplate = otherRoot.OpenSubKey(path);
        if (otherTemplate is not null) detail += " 同一类型在另一个账户范围也有模板，需要分别调整。";
        var entry = new MenuEntry(id, title, machine ? "全部账户" : "当前账户", extension.ToUpperInvariant() + " 文件", "新建模板", enabled, editable, managed, detail,
            id, parent, "new", RawName: raw, Registration: (machine ? "HKLM\\" : "HKCU\\") + path,
            State: !viable || type is null ? "关联不完整" : conflict ? "注册冲突" : enabled ? "已注册" : "已停用");
        result.Add(entry); targets[id] = new(entry, path, TreeHash(template), hive);
    }

    private bool Owned(string id, string path)
    {
        using var record = Registry.CurrentUser.OpenSubKey(journalPath + "\\" + id);
        return record?.GetValue("Path") as string == path;
    }

    public bool NeedsElevation(string[] ids)
    {
        Scan();
        return ids.Any(id => targets.TryGetValue(id, out var t) && t.Entry.CanToggle && t.Entry.Category == "new" && t.Hive == RegistryHive.LocalMachine);
    }

    public List<string> SetManyEnabled(string[] ids, bool enabled, bool machineOnly = false)
    {
        if (ids.Length > 512) throw new IOException("一次调整的项目过多。");
        using var guard = new Mutex(false, @"Local\TidyDrop.SpecialMenuOverrides");
        var locked = false;
        try
        {
            try { locked = guard.WaitOne(10000); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("另一处归序正在调整特殊菜单。");
            Scan();
            var errors = new List<string>();
            foreach (var id in ids.Distinct())
            {
                try
                {
                    if (!targets.TryGetValue(id, out var target) || !target.Entry.CanToggle) throw new IOException("项目已变化或不能调整，请刷新查看说明。");
                    if (machineOnly && (target.Hive != RegistryHive.LocalMachine || target.Entry.Category != "new")) throw new IOException("管理员助手只接受整机新建模板操作。");
                    if (target.Entry.Enabled == enabled && !target.Entry.Managed) continue;
                    if (target.Entry.Category == "sendto") ApplySendTo(target, enabled);
                    else ApplyNew(target, enabled);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
                { errors.Add(ex.Message); }
            }
            return errors;
        }
        finally { if (locked) guard.ReleaseMutex(); }
    }

    private void ApplySendTo(Target target, bool enabled)
    {
        // Revalidate file identity and attributes immediately before applying or restoring.
        if (FileHash(target.Path) != target.Hash) throw new IOException("发送到项目已变化，请刷新后重试。");
        var attrs = File.GetAttributes(target.Path);
        if (((attrs & FileAttributes.Hidden) == 0) != target.Entry.Enabled) throw new IOException("发送到项目的属性已变化，请刷新后重试。");
        using var record = enabled ? Registry.CurrentUser.OpenSubKey(journalPath + "\\" + target.Entry.Id) : null;
        if (enabled)
        {
            if (record is null || record.GetValue("Hash") as string != target.Hash) throw new IOException("发送到项目已被更新，恢复记录已保留。");
            var original = (FileAttributes)(int)record.GetValue("OriginalAttributes")!;
            var applied = (FileAttributes)(int)record.GetValue("AppliedAttributes")!;
            if (attrs != original && attrs != applied) throw new IOException("文件属性被其他工具更改，未覆盖。");
            if (attrs != original) File.SetAttributes(target.Path, original);
            record.Dispose(); Registry.CurrentUser.DeleteSubKey(journalPath + "\\" + target.Entry.Id, false);
        }
        else
        {
            if (target.Entry.Managed) throw new IOException("已有恢复记录，请先恢复此项。");
            using var journal = CreateRecord(target);
            journal.SetValue("OriginalAttributes", (int)attrs);
            journal.SetValue("AppliedAttributes", (int)(attrs | FileAttributes.Hidden)); journal.Flush();
            File.SetAttributes(target.Path, attrs | FileAttributes.Hidden);
        }
    }

    private void ApplyNew(Target target, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(target.Hive, RegistryView.Registry64);
        var parentPath = target.Path[..target.Path.LastIndexOf('\\')];
        using var parent = root.OpenSubKey(parentPath, true) ?? throw new IOException("模板注册已消失。");
        var from = enabled ? DisabledNew : "ShellNew";
        var to = enabled ? "ShellNew" : DisabledNew;
        using var record = enabled ? Registry.CurrentUser.OpenSubKey(journalPath + "\\" + target.Entry.Id) : null;
        using var current = parent.OpenSubKey(from);
        using var destination = parent.OpenSubKey(to);
        if (enabled)
        {
            if (record is null || record.GetValue("Hash") as string != target.Hash) throw new IOException("模板已被更新，未覆盖原软件注册。恢复记录已保留。");
            if (current is null && destination is not null && TreeHash(destination) == target.Hash)
            { record.Dispose(); Registry.CurrentUser.DeleteSubKey(journalPath + "\\" + target.Entry.Id, false); return; }
        }
        else if (target.Entry.Managed) throw new IOException("已有恢复记录，请先恢复此项。");
        if (current is null || TreeHash(current) != target.Hash || destination is not null) throw new IOException("模板注册或备份位置已变化，未覆盖。");
        if (!enabled) { using var journal = CreateRecord(target); journal.Flush(); }
        var error = RegRenameKey(parent.Handle.DangerousGetHandle(), from, to);
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
        if (enabled) { record!.Dispose(); Registry.CurrentUser.DeleteSubKey(journalPath + "\\" + target.Entry.Id, false); }
    }

    private RegistryKey CreateRecord(Target target)
    {
        var record = Registry.CurrentUser.CreateSubKey(journalPath + "\\" + target.Entry.Id);
        if (record.GetValue("Path") is not null) { record.Dispose(); throw new IOException("已有恢复记录，请先恢复。"); }
        record.SetValue("Path", target.Path); record.SetValue("Hash", target.Hash);
        record.SetValue("Category", target.Entry.Category); record.SetValue("Hive", target.Hive.ToString());
        return record;
    }
    private static string FileHash(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("发送到项目不是可管理的普通文件。");
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)) + ":" + File.GetCreationTimeUtc(path).Ticks;
    }
    private static string TreeHash(RegistryKey key)
    {
        var text = new StringBuilder();
        void Read(RegistryKey item, int depth)
        {
            if (depth > 16) throw new IOException("模板注册层级过深。");
            foreach (var name in item.GetValueNames().Order(StringComparer.Ordinal))
            {
                var value = item.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var encoded = System.Text.Json.JsonSerializer.Serialize(value);
                text.Append(System.Text.Json.JsonSerializer.Serialize(name)).Append(':').Append((int)item.GetValueKind(name)).Append(':').Append(encoded).Append('\n');
            }
            foreach (var name in item.GetSubKeyNames().Order(StringComparer.Ordinal))
            { text.Append('{').Append(System.Text.Json.JsonSerializer.Serialize(name)); using var child = item.OpenSubKey(name); if (child is not null) Read(child, depth + 1); text.Append('}'); }
        }
        Read(key, 0); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegRenameKey(IntPtr key, string oldName, string newName);
}
