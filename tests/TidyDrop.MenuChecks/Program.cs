using Microsoft.Win32;
using System.Diagnostics;
using TidyDrop.App;

if (args.Contains("--inventory"))
{
    var timer = Stopwatch.StartNew();
    var entries = new MenuRegistry().Scan();
    Console.WriteLine($"Read {entries.Count} registrations, {entries.Count(e => e.CanToggle)} editable in {timer.ElapsedMilliseconds} ms");
    return;
}
var name = "TidyDrop.MenuCheck." + Guid.NewGuid().ToString("N");
var root = @"Software\Classes\" + name;
var journal = @"Software\TidyDrop\MenuChecks\" + name;
var clsid = Guid.NewGuid().ToString("B");
var classRoot = @"Software\Classes\CLSID\" + clsid;
var executable = Path.Combine(Path.GetTempPath(), name + ".exe");
var extensionName = ".td" + Guid.NewGuid().ToString("N");
var material = Path.Combine(Path.GetTempPath(), name + extensionName);
var sendTo = Path.Combine(Path.GetTempPath(), name + ".sendto");
var specialJournal = journal + ".special";
File.WriteAllText(executable, "not an executable; metadata-only fixture");
File.WriteAllText(material, "menu selection fixture");
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
void MustFail(Action action, string message) { try { action(); } catch (IOException) { Console.WriteLine("PASS " + message); return; } throw new Exception(message); }
bool Visible(string verb = "fixture", string mode = "--find-verb", string? objectPath = null)
{
    var start = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var value in new[] { objectPath ?? material, verb, mode }) start.ArgumentList.Add(value);
    using var process = Process.Start(start)!;
    if (!process.WaitForExit(15000)) throw new Exception("Shell menu enumeration timed out");
    if (process.ExitCode is not (0 or 3)) throw new Exception("Shell menu enumeration failed");
    return process.ExitCode == 0;
}
try
{
    using (var association = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extensionName)) association.SetValue("", name);
    using (var fixture = Registry.CurrentUser.CreateSubKey(root + @"\shell\fixture"))
    {
        fixture.SetValue("", "测试 菜单, &文件");
        fixture.SetValue("Keep", "untouched");
        using var command = fixture.CreateSubKey("command");
        command.SetValue("", '"' + executable + "\" \"%1\"");
    }
    using (var system = Registry.CurrentUser.CreateSubKey(root + @"\shell\open\command")) system.SetValue("", '"' + executable + '"');
    using (var hidden = Registry.CurrentUser.CreateSubKey(root + @"\shell\external"))
    {
        hidden.SetValue("LegacyDisable", "owned elsewhere");
        using var command = hidden.CreateSubKey("command"); command.SetValue("", '"' + executable + '"');
    }
    using (var handler = Registry.CurrentUser.CreateSubKey(root + @"\shellex\ContextMenuHandlers\fixture")) handler.SetValue("", clsid, RegistryValueKind.ExpandString);
    using (var component = Registry.CurrentUser.CreateSubKey(classRoot + @"\InprocServer32")) component.SetValue("", executable);
    var manager = new MenuRegistry(journal, name);
    var all = manager.Scan();
    var item = all.Single(e => e.Title == "测试 菜单, 文件");
    Check(item.Enabled && item.CanToggle, "discover Unicode static command");
    Check(MenuLabels.Software("Tencent.QQDesktop") == "QQ" && MenuLabels.Command("AskM365Copilot", "Microsoft 365 / Copilot", true) == "询问 Copilot"
        && MenuLabels.Command("测试 中文菜单", "测试软件") == "测试 中文菜单", "friendly software labels preserve readable command names");
    Check(new MenuRegistry(journal).Scan().Any(e => e.Id == item.Id && e.Scope.Contains(extensionName)), "active file association is discovered with its extension");
    if (args.Length > 0) Check(Visible(), "Windows Shell actually enumerates the registered fixture command");
    Check(!all.Single(e => e.RawName == "open").CanToggle && !all.Single(e => e.Title == "external").CanToggle, "system verbs and externally disabled entries are protected");
    manager.SetEnabled(item.Id, false);
    if (args.Length > 0) Check(!Visible(), "Windows Shell excludes the disabled fixture command");
    using (var key = Registry.CurrentUser.OpenSubKey(root + @"\shell\fixture"))
        Check(key!.GetValueNames().Contains("LegacyDisable") && (string?)key.GetValue("Keep") == "untouched" && key.OpenSubKey("command") is not null, "disable preserves original command and unrelated values");
    var reopened = new MenuRegistry(journal, name);
    Check(reopened.Scan().Single(e => e.Id == item.Id) is { Enabled: false, Managed: true, CanToggle: true }, "restart retains recoverable ownership");
    reopened.SetEnabled(item.Id, true);
    if (args.Length > 0) Check(Visible(), "Windows Shell shows the restored fixture command");
    using (var key = Registry.CurrentUser.OpenSubKey(root + @"\shell\fixture")) Check(!key!.GetValueNames().Contains("LegacyDisable"), "restore removes only our flag");
    var extension = reopened.Scan().Single(e => e.Title == "fixture");
    Check(reopened.SetManyEnabled([item.Id, extension.Id, "invalid"], false).Count == 1
        && reopened.Scan().Where(e => e.Id == item.Id || e.Id == extension.Id).All(e => !e.Enabled && e.Managed), "grouped change reports partial failures and retains recovery per position");
    Check(reopened.SetManyEnabled([item.Id, extension.Id], true).Count == 0
        && reopened.Scan().Where(e => e.Id == item.Id || e.Id == extension.Id).All(e => e.Enabled && !e.Managed), "grouped recovery restores all original registrations");
    reopened.SetEnabled(extension.Id, false);
    using (var key = Registry.CurrentUser.OpenSubKey(root + @"\shellex\ContextMenuHandlers\fixture")) Check((string?)key!.GetValue("") == "-" + clsid, "scoped COM override stops only selected handler registration");
    reopened.SetEnabled(extension.Id, true);
    using (var key = Registry.CurrentUser.OpenSubKey(root + @"\shellex\ContextMenuHandlers\fixture")) Check((string?)key!.GetValue("") == clsid && key.GetValueKind("") == RegistryValueKind.ExpandString, "restore preserves original COM value and registry type");
    MustFail(() => reopened.SetEnabled("arbitrary/path", false), "reject unknown UI IDs");
    reopened.SetEnabled(item.Id, false);
    using (var command = Registry.CurrentUser.OpenSubKey(root + @"\shell\fixture\command", true)) command!.SetValue("", "changed.exe");
    MustFail(() => reopened.SetEnabled(item.Id, true), "software updates are not overwritten on restoration");
    using (var command = Registry.CurrentUser.OpenSubKey(root + @"\shell\fixture\command", true)) command!.SetValue("", '"' + executable + "\" \"%1\"");
    using (var key = Registry.CurrentUser.OpenSubKey(root + @"\shell\fixture", true)) key!.SetValue("LegacyDisable", "another tool");
    MustFail(() => reopened.SetEnabled(item.Id, true), "other tools' later changes are not overwritten");
    using (var key = Registry.CurrentUser.OpenSubKey(root + @"\shell\fixture", true)) key!.DeleteValue("LegacyDisable");
    reopened.SetEnabled(item.Id, true);
    Check(!reopened.Scan().Single(e => e.Id == item.Id).Managed, "write-ahead backup safely recovers when original is already restored");
    using (var parent = Registry.CurrentUser.CreateSubKey(root + @"\shell\group"))
    {
        parent.SetValue("MUIVerb", "父菜单"); parent.SetValue("SubCommands", "");
        foreach (var child in new[] { "first", "second" })
        { using var key = parent.CreateSubKey(@"shell\" + child); key.SetValue("MUIVerb", "相同显示名称"); using var cmd = key.CreateSubKey("command"); cmd.SetValue("", '"' + executable + "\" " + child); }
    }
    var nested = reopened.Scan();
    var parentEntry = nested.Single(e => e.Title == "父菜单");
    var children = nested.Where(e => e.ParentId == parentEntry.Id).ToArray();
    Check(children.Length == 2 && children[0].CommandKey != children[1].CommandKey && children.All(e => e.CanToggle), "private submenu preserves different command identities and parent hierarchy");
    using (var first = Registry.CurrentUser.OpenSubKey(root + @"\shell\group\shell\first", true)) first!.SetValue("MUIVerb", "归序测试子项甲");
    using (var second = Registry.CurrentUser.OpenSubKey(root + @"\shell\group\shell\second", true)) second!.SetValue("MUIVerb", "归序测试子项乙");
    if (args.Length > 0) Check(Visible("归序测试子项甲", "--find-menu-title"), "Windows Shell enumerates the private child before hiding");
    reopened.SetEnabled(children[0].Id, false);
    if (args.Length > 0) Check(!Visible("归序测试子项甲", "--find-menu-title") && Visible("归序测试子项乙", "--find-menu-title"), "Windows Shell actually hides only the selected private child");
    using (var key = Registry.CurrentUser.OpenSubKey(root + @"\shell\group\shell\first")) Check((int)key!.GetValue("HideBasedOnVelocityId")! == 0x639bc8 && key.GetValueKind("HideBasedOnVelocityId") == RegistryValueKind.DWord, "private child uses its own DWORD hiding strategy");
    reopened.SetEnabled(children[0].Id, true);
    Check(reopened.Scan().Where(e => e.ParentId == parentEntry.Id).All(e => e.Enabled && !e.Managed), "child recovery leaves sibling intact");
    var openWith = reopened.Scan().Single(e => e.Category == "openwith");
    reopened.SetEnabled(openWith.Id, false);
    using (var key = Registry.CurrentUser.OpenSubKey(root)) Check(key!.GetValueNames().Contains("NoOpenWith") && key.OpenSubKey(@"shell\open\command") is not null, "OpenWith hides its entry without changing default association or command");
    reopened.SetEnabled(openWith.Id, true);
    using (var key = Registry.CurrentUser.OpenSubKey(root)) Check(!key!.GetValueNames().Contains("NoOpenWith"), "OpenWith recovery removes only its own override");
    using (var hidden = Registry.CurrentUser.CreateSubKey(root + @"\shell\flagged"))
    { hidden.SetValue("CommandFlags", 8); using var cmd = hidden.CreateSubKey("command"); cmd.SetValue("", '"' + executable + '"'); }
    Check(reopened.Scan().Single(e => e.RawName == "flagged") is { Enabled: false, CanToggle: false }, "recognize foreign CommandFlags hiding without claiming its recovery");
    using (var template = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extensionName + @"\ShellNew"))
    { template.SetValue("NullFile", ""); template.SetValue("Data", new byte[] { 1, 7, 23 }, RegistryValueKind.Binary); using var config = template.CreateSubKey("Config"); config.SetValue("Keep", "%TEMP%", RegistryValueKind.ExpandString); }
    Directory.CreateDirectory(sendTo); var shortcut = Path.Combine(sendTo, "测试 入口.lnk"); File.WriteAllText(shortcut, "synthetic shortcut only");
    var templateTitle = "归序测试新建模板" + name[^6..];
    using (var type = Registry.CurrentUser.OpenSubKey(root, true)) type!.SetValue("", templateTitle);
    MenuNative.SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
    if (args.Length > 0) Check(Visible(templateTitle, "--find-background-title", sendTo), "Windows Shell actually includes the registered New template");
    var specials = new SpecialMenus(specialJournal, sendTo, extensionName);
    var specialEntries = specials.Scan(); var templateEntry = specialEntries.Single(e => e.Category == "new"); var sendEntry = specialEntries.Single(e => e.Category == "sendto");
    Check(specials.SetManyEnabled([templateEntry.Id, sendEntry.Id], false).Count == 0, "disable fixture New template and SendTo entry with separate strategies");
    MenuNative.SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
    if (args.Length > 0) Check(!Visible(templateTitle, "--find-background-title", sendTo), "Windows Shell excludes the renamed New template");
    using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extensionName)) Check(ext!.OpenSubKey("ShellNew") is null && ext.OpenSubKey("TidyDrop.DisabledShellNew") is not null, "ShellNew is atomically renamed instead of copied or deleted");
    Check(File.Exists(shortcut) && (File.GetAttributes(shortcut) & FileAttributes.Hidden) != 0, "SendTo shortcut content is preserved while hidden");
    specials = new SpecialMenus(specialJournal, sendTo, extensionName);
    Check(specials.Scan().All(e => !e.Enabled && e.Managed) && specials.SetManyEnabled([templateEntry.Id, sendEntry.Id], true).Count == 0, "special menu ownership survives reopening and both kinds restore");
    MenuNative.SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
    if (args.Length > 0) Check(Visible(templateTitle, "--find-background-title", sendTo), "Windows Shell shows the restored New template");
    using (var restored = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extensionName + @"\ShellNew"))
    { using var config = restored!.OpenSubKey("Config"); Check(((byte[])restored.GetValue("Data")!).SequenceEqual(new byte[] { 1, 7, 23 }) && config!.GetValueKind("Keep") == RegistryValueKind.ExpandString, "template restoration preserves binary values, subkeys and original types"); }
    specials.SetManyEnabled([sendEntry.Id], false);
    using (var stream = new FileStream(shortcut, FileMode.Open, FileAccess.Write)) { stream.SetLength(0); using var writer = new StreamWriter(stream); writer.Write("changed by another application"); }
    Check(specials.SetManyEnabled([sendEntry.Id], true).Count == 1 && specials.Scan().Single(e => e.Id == sendEntry.Id).Managed, "changed shortcut refuses restoration and retains its journal");
    Check(MenuService.Analyze(material).Locations.Contains(name) && MenuService.Analyze(sendTo).Locations.Contains("Directory"), "object analysis resolves current ProgID and directory sources");
    Check(MenuService.RunTemplateHelper(["--menu-template-change", "wrong-sid", "disable", templateEntry.Id]) != 0, "elevated helper rejects a different account and arbitrary arguments");
    // A COM server may show UI during creation, before any menu command is invoked.
    // These registered fixture identities must survive name processing untouched.
    var foreignDynamic = new MenuEntry("foreign-dynamic", "未知播放命令", "测试播放器", "音频", "动态命令", false, true, true,
        "保留恢复记录", "foreign-command", "audio", Clsid: clsid, Verb: "Play");
    var foreignPackaged = foreignDynamic with { Id = "foreign-package", Kind = "一级菜单", CanToggle = false, RawName = "OpaqueVerb" };
    var own = foreignDynamic with { Id = "own-copy", Clsid = "62F68386-2117-4D94-9405-C9B792BE8EEB", Title = "CopyPath" };
    var named = MenuMetadata.Read([foreignDynamic, foreignPackaged, own]);
    Check(named.Count == 3 && named[0] == foreignDynamic && named[1] == foreignPackaged,
        "passive names preserve unknown dynamic identities, states and restoration records without probing");
    Check(named[2] == own with { Title = "复制文件路径" }, "known copy-path label requires no component activation");
}
finally
{
    // All targets are fixed generated HKCU fixture keys, never production registrations.
    Registry.CurrentUser.DeleteSubKeyTree(root, false);
    Registry.CurrentUser.DeleteSubKeyTree(classRoot, false);
    Registry.CurrentUser.DeleteSubKeyTree(journal, false);
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + extensionName, false);
    Registry.CurrentUser.DeleteSubKeyTree(specialJournal, false);
    if (Directory.Exists(sendTo)) { foreach (var file in Directory.GetFiles(sendTo)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } Directory.Delete(sendTo); }
    File.Delete(executable);
    File.Delete(material);
}
internal static class MenuNative
{
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int eventId, uint flags, IntPtr first, IntPtr second);
}
