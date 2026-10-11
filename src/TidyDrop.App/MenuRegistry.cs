using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TidyDrop.App;

internal sealed record MenuEntry(string Id, string Title, string Source, string Scope, string Kind, bool Enabled, bool CanToggle, bool Managed, string Detail);

// Per-user overlays: never delete a software registration or write to HKLM.
// The journal is global to the account, so reinstalling/moving TidyDrop retains recovery.
internal sealed class MenuRegistry(string journalPath = @"Software\TidyDrop\MenuOverrides", string? onlyClass = null)
{
    private sealed record Target(string Path, string ValueName, string Fingerprint, MenuEntry Entry);
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
                var path = location + @"\shell\" + verb;
                using var key = classes.OpenSubKey(path);
                if (key is null) continue;
                using var command = key.OpenSubKey("command");
                var executable = command?.GetValue("") as string ?? "";
                var handler = key.GetValue("ExplorerCommandHandler") as string ?? "";
                var subcommands = key.GetValue("SubCommands") as string ?? "";
                var name = key.GetValue("MUIVerb") as string ?? key.GetValue("") as string ?? verb;
                var title = ResolveLabel(name, verb);
                var fingerprint = executable + "|" + handler + "|" + subcommands;
                var server = handler.Length > 0 ? Server(classes, handler) : ExecutablePath(executable);
                var protectedItem = SystemVerbs.Contains(verb) || verb.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) || verb.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || IsWindowsComponent(server) || fingerprint == "||" || server.Length == 0 || !File.Exists(server);
                var enabled = !HasValue(key, "LegacyDisable") && !HasValue(key, "ProgrammaticAccessOnly");
                Add(path, "LegacyDisable", fingerprint, title, Source(server, verb), Scope(location), handler.Length > 0 ? "命令扩展" : "更多选项", enabled, protectedItem, result);
            }
            using var handlers = classes.OpenSubKey(location + @"\shellex\ContextMenuHandlers");
            foreach (var name in handlers?.GetSubKeyNames() ?? [])
            {
                var path = location + @"\shellex\ContextMenuHandlers\" + name;
                using var key = classes.OpenSubKey(path);
                var raw = key?.GetValue("") as string ?? "";
                if (!Guid.TryParse(raw.TrimStart('-'), out var guid)) continue;
                var clsid = guid.ToString("B");
                var server = Server(classes, clsid);
                using var component = classes.OpenSubKey(@"CLSID\" + clsid);
                var title = component?.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(title)) title = name;
                var blocked = IsBlocked(clsid);
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var underlying = machine.OpenSubKey(@"Software\Classes\" + path);
                var machineValue = underlying?.GetValue("", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                Add(path, "", clsid + "|" + server + "|" + machineValue, title, Source(server, name), Scope(location), "更多选项", !raw.StartsWith('-') && !blocked,
                    server.Length == 0 || !File.Exists(server) || IsWindowsComponent(server) || blocked, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { /* Other accessible locations still load. */ }
    }

    private void Add(string path, string valueName, string fingerprint, string title, string source, string scope, string kind, bool enabled, bool protectedItem, List<MenuEntry> result)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var journal = Registry.CurrentUser.OpenSubKey(journalPath + "\\" + id);
        var managed = journal?.GetValue("Path") as string == path;
        var canToggle = !protectedItem && (enabled || managed);
        var detail = protectedItem ? "系统组件、受其他工具限制或注册不完整，仅供查看。"
            : !enabled && !managed ? "已被其他软件隐藏，请在原软件中恢复。"
            : valueName.Length == 0 ? "此组件可能生成多个菜单项；开关只影响这个出现位置。"
            : "停用后保留原软件的注册信息，随时可恢复。";
        source = MenuLabels.Software(source);
        title = MenuLabels.Command(title, source);
        var entry = new MenuEntry(id, title, source, scope, kind, enabled, canToggle, managed, detail);
        targets[id] = new(path, valueName, fingerprint, entry);
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
                    var marker = discovered.ValueName.Length == 0 ? @"\shellex\ContextMenuHandlers\" : @"\shell\";
                    var location = discovered.Path[..discovered.Path.IndexOf(marker, StringComparison.OrdinalIgnoreCase)];
                    targets.Remove(id);
                    ReadLocation(classes, location, []);
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
            var applied = journal.GetValue("Applied") as string ?? "";
            var actual = current?.GetValue(target.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var hadValue = (int)(journal.GetValue("HadValue") ?? 0) == 1;
            var original = journal.GetValue("Original", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (target.Entry.Enabled && ((!hadValue && actual is null) || (hadValue && Equals(actual, original))))
            {
                // Crash before applying the overlay, or the exact original was restored elsewhere.
                journal.Dispose(); Registry.CurrentUser.DeleteSubKey(journalKey, false); return;
            }
            if (actual as string != applied)
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
            if (hadValue && kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) throw new IOException("菜单注册格式不受支持，未修改。");
            var applied = target.ValueName.Length > 0 ? "" : "-" + target.Fingerprint.Split('|')[0];
            // Write-ahead backup survives a crash between the backup and overlay writes.
            using var journal = Registry.CurrentUser.CreateSubKey(journalKey);
            if (journal.GetValue("Path") is not null) throw new IOException("已有未完成的恢复记录，请先恢复此项。");
            journal.SetValue("Path", target.Path);
            journal.SetValue("Fingerprint", target.Fingerprint);
            journal.SetValue("HadKey", previous is null ? 0 : 1);
            journal.SetValue("HadValue", hadValue ? 1 : 0);
            journal.SetValue("OriginalKind", (int)kind);
            if (original is not null) journal.SetValue("Original", original, kind);
            journal.SetValue("Applied", applied);
            journal.Flush();
            try
            {
                using var overlay = Registry.CurrentUser.CreateSubKey(userPath);
                overlay.SetValue(target.ValueName, applied, RegistryValueKind.String);
                overlay.Flush();
            }
            catch { journal.Dispose(); Registry.CurrentUser.DeleteSubKey(journalKey, false); throw; }
        }
    }

    private static bool HasValue(RegistryKey key, string name) => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    private static string Server(RegistryKey classes, string clsid)
    {
        using var key = classes.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32");
        return ExecutablePath(key?.GetValue("") as string ?? "");
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
        "Drive" => "磁盘", @"Directory\Background" or "DesktopBackground" or @"DesktopBackground\Background" => "空白处",
        _ => "特定类型 · " + (fileTypes.TryGetValue(location, out var types) ? string.Join("、", types.Order(StringComparer.OrdinalIgnoreCase)) : MenuLabels.FileType(location))
    };
    private static string ResolveLabel(string value, string fallback)
    {
        if (!value.StartsWith('@')) return value.Replace("&", "");
        var text = new StringBuilder(512);
        return SHLoadIndirectString(value, text, (uint)text.Capacity, IntPtr.Zero) == 0 ? text.ToString().Replace("&", "") : fallback;
    }
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint size, IntPtr reserved);
}
