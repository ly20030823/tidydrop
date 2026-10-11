using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TidyDrop.App;

internal sealed record MenuEntry(string Id, string Title, string Source, string Scope, string Kind, bool Enabled, bool CanToggle, bool Managed, string Detail,
    string CommandKey = "", string Location = "", string Category = "context", string ParentId = "", string RawName = "", string Registration = "", string State = "", string Clsid = "", string Verb = "");

// Per-user overlays: never delete a software registration or write to HKLM.
// The journal is global to the account, so reinstalling/moving TidyDrop retains recovery.
internal sealed class MenuRegistry(string journalPath = @"Software\TidyDrop\MenuOverrides", string? onlyClass = null)
{
    private sealed record Target(string Path, string ValueName, string Fingerprint, MenuEntry Entry, string Location, object Applied, RegistryValueKind AppliedKind);
    private readonly Dictionary<string, Target> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> fileTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SystemVerbs = new(StringComparer.OrdinalIgnoreCase)
    { "open", "openas", "opennewwindow", "opennewprocess", "explore", "runas", "runasuser", "print", "printto", "edit", "find", "properties", "delete", "rename", "cut", "copy", "paste", "pintohome", "pintohomefile", "PinToStartScreen", "Windows.ModernShare", "UpdateEncryptionSettings", "format", "eject", "cmd", "PowerShell", "setdesktopwallpaper", "Preview", "play", "AddToPlaylist", "extract", "mount", "burn", "restore", "CreateShortcut" };

    public List<MenuEntry> Scan()
    {
        targets.Clear();
        fileTypes.Clear();
        var result = new List<MenuEntry>();
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        var roots = onlyClass is null ? ActiveRoots(classes) : [onlyClass];
        foreach (var root in roots)
        {
            if (root is "CLSID" or "Interface" or "TypeLib" or "AppID") continue;
            ReadLocation(classes, root, result);
            if (onlyClass is null && root is "Directory" or "DesktopBackground") ReadLocation(classes, root + @"\Background", result);
            if (onlyClass is null && root == "SystemFileAssociations")
            {
                using var associations = classes.OpenSubKey(root);
                foreach (var name in associations?.GetSubKeyNames() ?? []) ReadLocation(classes, root + "\\" + name, result);
            }
        }
        ReadOpenWith(classes, result, onlyClass);
        return result.OrderBy(e => e.Source).ThenBy(e => e.Title).ThenBy(e => e.Scope).ToList();
    }

    private string[] ActiveRoots(RegistryKey classes)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "*", "AllFilesystemObjects", "Directory", "Folder", "Drive", "DesktopBackground", "SystemFileAssociations" };
        foreach (var extension in classes.GetSubKeyNames().Where(n => n.StartsWith('.')))
        {
            roots.Add(extension);
            try
            {
            using var key = classes.OpenSubKey(extension);
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\UserChoice");
            var type = choice?.GetValue("ProgId") as string ?? key?.GetValue("") as string;
            if (string.IsNullOrWhiteSpace(type) || type.Contains('\\')) continue;
            roots.Add(type);
            if (!fileTypes.TryGetValue(type, out var extensions)) fileTypes[type] = extensions = [];
            extensions.Add(extension);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        // Keep our recovery records discoverable even after a file association changes.
        using var journal = Registry.CurrentUser.OpenSubKey(journalPath);
        foreach (var id in journal?.GetSubKeyNames() ?? [])
        {
            using var record = journal!.OpenSubKey(id);
            var path = record?.GetValue("Path") as string;
            if (path is not null && (path.Contains(@"\shell\") || path.Contains(@"\shellex\ContextMenuHandlers\"))) roots.Add(path.Split('\\')[0]);
        }
        return roots.ToArray();
    }

    private void ReadLocation(RegistryKey classes, string location, List<MenuEntry> result)
    {
        try
        {
            using var shell = classes.OpenSubKey(location + @"\shell");
            foreach (var verb in shell?.GetSubKeyNames() ?? [])
            {
                ReadVerb(classes, location + @"\shell\" + verb, location, result, "", 0, false);
            }
            foreach (var part in new[] { "ContextMenuHandlers", "-ContextMenuHandlers" })
            {
            using var handlers = classes.OpenSubKey(location + @"\shellex\" + part);
            foreach (var name in handlers?.GetSubKeyNames() ?? [])
            {
                var path = location + @"\shellex\" + part + "\\" + name;
                using var key = classes.OpenSubKey(path);
                var raw = key?.GetValue("") as string ?? "";
                if (!Guid.TryParse(raw.TrimStart('-'), out var guid) && !Guid.TryParse(name, out guid)) continue;
                var clsid = guid.ToString("B");
                var server = Server(classes, clsid);
                using var component = classes.OpenSubKey(@"CLSID\" + clsid);
                var title = component?.GetValue("LocalizedString") as string ?? component?.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(title)) title = name;
                title = ResolveLabel(title, name);
                var blocked = IsBlocked(clsid);
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var underlying = machine.OpenSubKey(@"Software\Classes\" + path);
                var machineValue = underlying?.GetValue("", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var off = part.StartsWith('-') || raw.StartsWith('-') || blocked;
                var source = MenuLabels.Software(Source(server, name));
                title = MenuLabels.Component(title, source, clsid);
                Add(path, "", clsid + "|" + server + "|" + machineValue, title, source, Scope(location), "扩展组件", !off,
                    server.Length == 0 || !File.Exists(server) || IsWindowsComponent(server) || blocked || part.StartsWith('-'), result, location,
                    commandKey: "extension:" + clsid + "|" + server, rawName: name, clsid: clsid);
            }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { /* Other accessible locations still load. */ }
    }

    private void ReadVerb(RegistryKey classes, string path, string location, List<MenuEntry> result, string parent, int depth, bool shared)
    {
        if (depth > 6 || result.Count > 15000) return;
        using var key = classes.OpenSubKey(path);
        if (key is null) return;
        var verb = path.Split('\\').Last();
        using var command = key.OpenSubKey("command");
        var executable = command?.GetValue("") as string ?? "";
        var handler = key.GetValue("ExplorerCommandHandler") as string ?? command?.GetValue("DelegateExecute") as string ?? "";
        var subcommands = key.GetValue("SubCommands") as string ?? "";
        var extended = key.GetValue("ExtendedSubCommandsKey") as string ?? "";
        using var children = key.OpenSubKey("shell");
        var hasChildren = children?.SubKeyCount > 0 || HasValue(key, "SubCommands") || extended.Length > 0;
        var name = key.GetValue("MUIVerb") as string ?? key.GetValue("") as string ?? verb;
        var title = ResolveLabel(name, verb);
        // Preserve v0.6.3 fingerprints for existing commands and their recovery records.
        var fingerprint = executable + "|" + handler + "|" + subcommands;
        if (extended.Length > 0 || children?.SubKeyCount > 0) fingerprint += "|submenu:" + extended + ":" + string.Join(';', children?.GetSubKeyNames() ?? []);
        var server = handler.Length > 0 ? Server(classes, handler) : ExecutablePath(executable);
        var protectedItem = shared || SystemVerbs.Contains(verb) || verb.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)
            || verb.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || IsWindowsComponent(server)
            || (!hasChildren && (fingerprint == "||" || server.Length == 0 || !File.Exists(server)));
        var flags = Number(key, "CommandFlags");
        var velocity = Number(key, "HideBasedOnVelocityId");
        var enabled = velocity != 0x639bc8 && (parent.Length > 0 || (!HasValue(key, "LegacyDisable") && !HasValue(key, "ProgrammaticAccessOnly") && (flags & 8) == 0));
        var hiddenElsewhere = velocity == 0x639bc8 || (parent.Length == 0 && ((flags & 8) != 0 || HasValue(key, "ProgrammaticAccessOnly")));
        var kind = hasChildren ? "子菜单" : handler.Length > 0 ? "动态命令" : "静态命令";
        Add(path, parent.Length > 0 ? "HideBasedOnVelocityId" : "LegacyDisable", fingerprint, title, Source(server, verb), Scope(location), kind, enabled,
            protectedItem || (hiddenElsewhere && !IsOwned(path)), result, location, parent, "verb:" + verb + "|" + fingerprint,
            name, clsid: handler, verb: verb, shared: shared);
        var id = Id(path);
        foreach (var child in children?.GetSubKeyNames() ?? []) ReadVerb(classes, path + @"\shell\" + child, location, result, id, depth + 1, shared);
        if (extended.Length > 0 && !extended.Contains(".."))
        {
            using var extra = classes.OpenSubKey(extended + @"\shell");
            foreach (var child in extra?.GetSubKeyNames() ?? []) ReadVerb(classes, extended + @"\shell\" + child, location, result, id, depth + 1, true);
        }
        foreach (var reference in subcommands.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(60))
        {
            if (reference.Contains('\\') || reference == "|") continue;
            // Read CommandStore through a separate HKLM root; shared commands stay read-only.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            ReadVerb(machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell\" + reference, location, result, id, depth + 1, true);
        }
    }

    private void ReadOpenWith(RegistryKey classes, List<MenuEntry> result, string? onlyPath = null)
    {
        using var applications = classes.OpenSubKey("Applications");
        using var journal = Registry.CurrentUser.OpenSubKey(journalPath);
        var retained = new List<string>();
        foreach (var id in journal?.GetSubKeyNames() ?? [])
        { using var record = journal!.OpenSubKey(id); if (record?.GetValue("ValueName") as string == "NoOpenWith" && record.GetValue("Path") is string path) retained.Add(path); }
        var paths = onlyPath is not null ? [onlyPath] : (applications?.GetSubKeyNames() ?? []).Select(n => @"Applications\" + n)
            .Concat(fileTypes.Keys).Concat(retained).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in paths)
        {
            try
            {
                using var key = classes.OpenSubKey(path);
                using var command = key?.OpenSubKey(@"shell\open\command");
                var raw = command?.GetValue("") as string ?? "";
                var server = ExecutablePath(raw);
                if (raw.Length == 0) continue;
                var name = key?.GetValue("FriendlyAppName") as string ?? key?.GetValue("FriendlyTypeName") as string ?? key?.GetValue("") as string ?? Path.GetFileNameWithoutExtension(server);
                var title = ResolveLabel(name, Path.GetFileNameWithoutExtension(server));
                Add(path, "NoOpenWith", raw, title, Source(server, title), path.StartsWith(@"Applications\") ? "应用列表" : Scope(path), "打开方式", !HasValue(key!, "NoOpenWith"),
                    !File.Exists(server) || IsWindowsComponent(server), result, path, commandKey: "openwith:" + path, rawName: name, category: "openwith");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
    }

    private bool IsOwned(string path)
    {
        using var journal = Registry.CurrentUser.OpenSubKey(journalPath + "\\" + Id(path));
        return journal?.GetValue("Path") as string == path;
    }
    internal static string Id(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));

    private void Add(string path, string valueName, string fingerprint, string title, string source, string scope, string kind, bool enabled, bool protectedItem, List<MenuEntry> result,
        string location, string parent = "", string commandKey = "", string rawName = "", string category = "context", string clsid = "", string verb = "", bool shared = false)
    {
        var id = Id(path);
        using var journal = Registry.CurrentUser.OpenSubKey(journalPath + "\\" + id);
        var managed = journal?.GetValue("Path") as string == path;
        var canToggle = !protectedItem && (enabled || managed);
        var detail = shared ? "公共子命令可能被其他菜单引用，当前仅查看；可以调整所属父菜单。" : protectedItem ? "系统基础组件、受其他工具限制或注册不完整，仅供查看。"
            : !enabled && !managed ? "已被其他软件隐藏，请在原软件中恢复。"
            : valueName.Length == 0 ? "此组件可能生成多个菜单项；开关只影响这个出现位置。"
            : category == "openwith" ? "隐藏此应用/关联的打开方式入口，不更改默认打开程序。应用和文件类型可能各有一条注册，需要分别调整。"
            : parent.Length > 0 ? "只调整这个子命令；父菜单停用时子命令也不会显示。"
            : "停用后保留原软件的注册信息，随时可恢复。";
        source = MenuLabels.Software(source);
        title = MenuLabels.Command(title, source);
        var conditional = kind == "动态命令" || kind == "扩展组件" || category == "openwith";
        var entry = new MenuEntry(id, title, source, scope, kind, enabled, canToggle, managed, detail, commandKey, location, category, parent, rawName,
            (shared && path.StartsWith("SOFTWARE\\") ? "HKLM\\" : "HKCR\\") + path, !enabled ? "已停用" : conditional ? "已注册 · 条件显示" : "已启用", clsid, verb);
        var applied = valueName == "HideBasedOnVelocityId" ? (object)0x639bc8 : valueName.Length > 0 ? "" : "-" + fingerprint.Split('|')[0];
        targets[id] = new(path, valueName, fingerprint, entry, location, applied, applied is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        result.Add(entry);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var errors = SetManyEnabled([id], enabled);
        if (errors.Count > 0) throw new IOException(errors[0]);
    }

    public List<string> SetManyEnabled(string[] ids, bool enabled)
    {
        if (ids.Length > 512) throw new IOException("一次调整的位置过多，请缩小筛选范围。");
        using var guard = new Mutex(false, @"Local\TidyDrop.MenuOverrides");
        var locked = false;
        try
        {
            try { locked = guard.WaitOne(10000); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("另一处归序正在修改菜单，请稍后重试。");
            // Discover once under the lock; re-read each affected location before writing.
            Scan();
            var errors = new List<string>();
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            foreach (var id in ids.Distinct())
            {
                try
                {
                    if (!targets.TryGetValue(id, out var discovered)) throw new IOException("此项目不能由归序调整，请刷新后查看说明。");
                    targets.Remove(id);
                    if (discovered.Entry.Category == "openwith") ReadOpenWith(classes, [], discovered.Path);
                    else ReadLocation(classes, discovered.Location, []);
                    if (!targets.TryGetValue(id, out var current) || !current.Entry.CanToggle || current.Fingerprint != discovered.Fingerprint)
                        throw new IOException("菜单状态已变化，请刷新后重试。");
                    Apply(id, current, enabled);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { errors.Add(ex.Message); }
            }
            return errors;
        }
        finally { if (locked) guard.ReleaseMutex(); }
    }

    private void Apply(string id, Target target, bool enabled)
    {
        if (target.Entry.Enabled == enabled && !target.Entry.Managed) return;
        var userPath = @"Software\Classes\" + target.Path;
        var journalKey = journalPath + "\\" + id;
        if (enabled)
        {
            using var journal = Registry.CurrentUser.OpenSubKey(journalKey);
            if (journal is null || journal.GetValue("Fingerprint") as string != target.Fingerprint) throw new IOException("所属软件已更新注册信息。请在原软件中恢复菜单，归序保留了恢复记录。");
            using var current = Registry.CurrentUser.OpenSubKey(userPath, true);
            var applied = journal.GetValue("Applied", "", RegistryValueOptions.DoNotExpandEnvironmentNames);
            var actual = current?.GetValue(target.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var hadValue = (int)(journal.GetValue("HadValue") ?? 0) == 1;
            var original = journal.GetValue("Original", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (target.Entry.Enabled && ((!hadValue && actual is null) || (hadValue && Equals(actual, original))))
            {
                // Crash before applying the overlay, or the exact original was restored elsewhere.
                journal.Dispose(); Registry.CurrentUser.DeleteSubKey(journalKey, false); return;
            }
            if (!Equals(actual, applied))
                throw new IOException("菜单被其他工具更改，归序未覆盖它。请在原软件中恢复。");
            if (current is null) throw new IOException("菜单注册已被移除，未覆盖原软件信息。");
            if ((int)(journal.GetValue("HadValue") ?? 0) == 1)
                current.SetValue(target.ValueName, journal.GetValue("Original", "", RegistryValueOptions.DoNotExpandEnvironmentNames)!, (RegistryValueKind)(int)journal.GetValue("OriginalKind")!);
            else current.DeleteValue(target.ValueName, false);
            // Delete only an empty leaf created by us; never remove software-owned subkeys.
            var removeEmpty = (int)(journal.GetValue("HadKey") ?? 1) == 0 && current.SubKeyCount == 0 && current.ValueCount == 0;
            current.Dispose();
            if (removeEmpty) Registry.CurrentUser.DeleteSubKey(userPath, false);
            journal.Dispose();
            Registry.CurrentUser.DeleteSubKey(journalKey, false);
        }
        else
        {
            if (target.Entry.Managed) throw new IOException("已有恢复记录，请先恢复此项，再尝试停用。");
            using var previous = Registry.CurrentUser.OpenSubKey(userPath);
            var hadValue = previous is not null && HasValue(previous, target.ValueName);
            var original = hadValue ? previous!.GetValue(target.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
            var kind = hadValue ? previous!.GetValueKind(target.ValueName) : RegistryValueKind.String;
            if (hadValue && kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) && !(kind == RegistryValueKind.DWord && target.AppliedKind == RegistryValueKind.DWord)) throw new IOException("菜单注册格式不受支持，未修改。");
            var applied = target.Applied;
            // Write-ahead backup survives a crash between the backup and overlay writes.
            using var journal = Registry.CurrentUser.CreateSubKey(journalKey);
            if (journal.GetValue("Path") is not null) throw new IOException("已有未完成的恢复记录，请先恢复此项。");
            journal.SetValue("Path", target.Path);
            journal.SetValue("ValueName", target.ValueName);
            journal.SetValue("Fingerprint", target.Fingerprint);
            journal.SetValue("HadKey", previous is null ? 0 : 1);
            journal.SetValue("HadValue", hadValue ? 1 : 0);
            journal.SetValue("OriginalKind", (int)kind);
            if (original is not null) journal.SetValue("Original", original, kind);
            journal.SetValue("Applied", applied, target.AppliedKind);
            journal.Flush();
            try
            {
                using var overlay = Registry.CurrentUser.CreateSubKey(userPath);
                overlay.SetValue(target.ValueName, applied, target.AppliedKind);
                overlay.Flush();
            }
            catch { journal.Dispose(); Registry.CurrentUser.DeleteSubKey(journalKey, false); throw; }
        }
    }

    private static bool HasValue(RegistryKey key, string name) => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    private static int Number(RegistryKey key, string name)
    { try { return Convert.ToInt32(key.GetValue(name, 0)); } catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; } }
    private static string Server(RegistryKey classes, string clsid)
    {
        foreach (var name in new[] { "InprocServer32", "LocalServer32" })
        {
            using var key = classes.OpenSubKey(@"CLSID\" + clsid + "\\" + name);
            var file = ExecutablePath(key?.GetValue("CodeBase") as string ?? key?.GetValue("") as string ?? "");
            if (file.Length > 0) return file;
        }
        return "";
    }
    private static bool IsBlocked(string clsid)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var blocked = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked");
            if (blocked is not null && HasValue(blocked, clsid)) return true;
        }
        return false;
    }
    private static string ExecutablePath(string value)
    {
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        if (value.StartsWith('"')) return value.Split('"').ElementAtOrDefault(1) ?? "";
        foreach (var suffix in new[] { ".exe", ".dll" })
        {
            var end = value.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (end >= 0) return value[..(end + suffix.Length)];
        }
        return "";
    }
    private static bool IsWindowsComponent(string file)
    {
        if (file.Length == 0) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        if (file.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return true;
        try { return File.Exists(file) && (FileVersionInfo.GetVersionInfo(file).CompanyName ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    private static string Source(string file, string fallback)
    {
        try
        {
            if (File.Exists(file))
            {
                var info = FileVersionInfo.GetVersionInfo(file);
                if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName;
            }
            return file.Length > 0 ? Path.GetFileNameWithoutExtension(file) : "未识别的软件";
        }
        catch { return fallback; }
    }
    private string Scope(string location) => location switch
    {
        "*" => "所有文件", "AllFilesystemObjects" => "文件与文件夹", "Directory" or "Folder" => "文件夹",
        "Drive" => "磁盘", @"Directory\Background" => "文件夹空白处", "DesktopBackground" or @"DesktopBackground\Background" => "桌面空白处",
        _ => "特定类型 · " + (fileTypes.TryGetValue(location, out var types) ? string.Join("、", types.Order(StringComparer.OrdinalIgnoreCase)) : MenuLabels.FileType(location))
    };
    internal static string ResolveLabel(string value, string fallback)
    {
        if (!value.StartsWith('@')) return value.Replace("&", "");
        var text = new StringBuilder(512);
        return SHLoadIndirectString(value, text, (uint)text.Capacity, IntPtr.Zero) == 0 ? text.ToString().Replace("&", "") : fallback;
    }
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint size, IntPtr reserved);
}
