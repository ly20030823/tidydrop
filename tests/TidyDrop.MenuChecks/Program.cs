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
File.WriteAllText(executable, "not an executable; metadata-only fixture");
File.WriteAllText(material, "menu selection fixture");
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
void MustFail(Action action, string message) { try { action(); } catch (IOException) { Console.WriteLine("PASS " + message); return; } throw new Exception(message); }
bool Visible()
{
    var start = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var value in new[] { material, "fixture", "--find-verb" }) start.ArgumentList.Add(value);
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
    if (args.Length > 0) Check(Visible(), "Windows Shell actually enumerates the registered fixture command");
    Check(!all.Single(e => e.Title == "open").CanToggle && !all.Single(e => e.Title == "external").CanToggle, "system verbs and externally disabled entries are protected");
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
}
finally
{
    // All targets are fixed generated HKCU fixture keys, never production registrations.
    Registry.CurrentUser.DeleteSubKeyTree(root, false);
    Registry.CurrentUser.DeleteSubKeyTree(classRoot, false);
    Registry.CurrentUser.DeleteSubKeyTree(journal, false);
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + extensionName, false);
    File.Delete(executable);
    File.Delete(material);
}
