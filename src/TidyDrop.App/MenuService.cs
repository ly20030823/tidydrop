using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace TidyDrop.App;

internal sealed record MenuAnalysis(string Name, string[] Locations, string Detail);
internal sealed class MenuService
{
    private readonly MenuRegistry registry = new();
    private readonly SpecialMenus special = new();
    public List<MenuEntry> Scan() => [.. registry.Scan(), .. special.Scan()];
    public async Task<List<string>> SetManyEnabled(string[] ids, bool enabled)
    {
        var errors = new List<string>();
        var ordinary = ids.Where(id => !id.StartsWith("special:")).ToArray();
        if (ordinary.Length > 0) errors.AddRange(await Task.Run(() => registry.SetManyEnabled(ordinary, enabled)));
        var specialIds = ids.Where(id => id.StartsWith("special:")).ToArray();
        if (specialIds.Length == 0) return errors;
        var machine = await Task.Run(() => special.NeedsElevation(specialIds));
        if (!machine || IsAdministrator()) errors.AddRange(await Task.Run(() => special.SetManyEnabled(specialIds, enabled)));
        else
        {
            var all = await Task.Run(special.Scan);
            var elevated = specialIds.Where(id => all.Any(e => e.Id == id && e.Category == "new" && e.Source == "全部账户")).ToArray();
            var local = specialIds.Except(elevated).ToArray();
            if (local.Length > 0) errors.AddRange(await Task.Run(() => special.SetManyEnabled(local, enabled)));
            // No arbitrary registry path enters the elevated helper. It rediscoveries IDs itself.
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--menu-template-change", WindowsIdentity.GetCurrent().User!.Value, enabled ? "enable" : "disable", string.Join(',', elevated) }) start.ArgumentList.Add(arg);
            try
            {
                using var process = Process.Start(start) ?? throw new IOException("管理员助手未启动。");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) errors.Add("整机模板未全部调整，可能取消了管理员确认或模板已变化。请刷新查看状态和恢复记录。");
            }
            catch (System.ComponentModel.Win32Exception ex) { errors.Add(ex.NativeErrorCode == 1223 ? "已取消管理员确认，整机模板未调整。" : ex.Message); }
        }
        return errors;
    }

    internal static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    internal static int RunTemplateHelper(string[] args)
    {
        if (args.Length != 4 || args[0] != "--menu-template-change" || args[1] != WindowsIdentity.GetCurrent().User?.Value || !IsAdministrator()
            || args[2] is not ("enable" or "disable")) return 2;
        var ids = args[3].Split(',');
        if (ids.Length is 0 or > 512 || ids.Any(id => !id.StartsWith("special:") || id.Length != 72 || !id[8..].All(Uri.IsHexDigit))) return 2;
        try { return new SpecialMenus().SetManyEnabled(ids, args[2] == "enable", machineOnly: true).Count == 0 ? 0 : 1; }
        catch { return 1; }
    }

    public static MenuAnalysis Analyze(string path)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(path))
        {
            locations.UnionWith(["Folder", "AllFilesystemObjects", Path.GetPathRoot(path)?.TrimEnd('\\') == path.TrimEnd('\\') ? "Drive" : "Directory"]);
        }
        else if (File.Exists(path))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            locations.UnionWith(["*", "AllFilesystemObjects", extension, @"SystemFileAssociations\" + extension]);
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            using var key = classes.OpenSubKey(extension);
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\UserChoice");
            var progid = choice?.GetValue("ProgId") as string ?? key?.GetValue("") as string;
            if (!string.IsNullOrWhiteSpace(progid)) locations.Add(progid);
            var perceived = key?.GetValue("PerceivedType") as string;
            if (!string.IsNullOrWhiteSpace(perceived)) locations.Add(@"SystemFileAssociations\" + perceived);
        }
        else throw new IOException("选择的对象已不存在。");
        return new(Path.GetFileName(path.TrimEnd('\\')), locations.ToArray(), "已筛选与这个对象相关的注册来源。软件仍可能按文件内容、选择数量等条件决定是否显示；此列表不执行右键命令。");
    }
}
