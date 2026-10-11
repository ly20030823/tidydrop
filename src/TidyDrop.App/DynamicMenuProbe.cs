using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TidyDrop.App;

// Query third-party code only in a disposable process, never on the main UI thread.
// Metadata methods only: Invoke is deliberately absent from every call site.
internal static class DynamicMenuProbe
{
    private sealed record Child(string Title, string Name);
    private sealed record Result(string Title, Child[] Children);
    private static readonly Dictionary<string, Result?> cache = [];
    internal static void ClearCache() { lock (cache) cache.Clear(); }

    public static async Task<List<MenuEntry>> Enrich(List<MenuEntry> entries, CancellationToken cancellation = default)
    {
        var candidates = entries.Where(e => e.Kind is "一级菜单" or "动态命令" && Guid.TryParse(e.Clsid, out _)).GroupBy(e => e.CommandKey).ToArray();
        using var slots = new SemaphoreSlim(2);
        var elapsed = Stopwatch.StartNew();
        var results = await Task.WhenAll(candidates.Select(async group => {
            await slots.WaitAsync(cancellation);
            try
            {
                Result? result;
                lock (cache) { if (cache.TryGetValue(group.Key, out result)) return (group.Key, result); }
                if (elapsed.ElapsedMilliseconds > 15000) return (group.Key, (Result?)null);
                result = await Probe(group.First(), cancellation); cancellation.ThrowIfCancellationRequested(); lock (cache) cache[group.Key] = result;
                return (group.Key, result);
            }
            finally { slots.Release(); }
        }));
        var names = results.ToDictionary(r => r.Key, r => r.Item2);
        var output = new List<MenuEntry>();
        foreach (var entry in entries)
        {
            if (!names.TryGetValue(entry.CommandKey, out var found) || found is null) { output.Add(entry); continue; }
            output.Add(entry with { Title = found.Title, Detail = entry.Detail + " 名称由组件接口读取；实际内容仍可能随选中的对象变化。" });
            foreach (var child in found.Children)
                output.Add(entry with { Id = entry.Id + ":child:" + child.Name, Title = child.Title, CommandKey = entry.CommandKey + ":child:" + child.Name,
                    ParentId = entry.Id, CanToggle = false, Managed = false, Kind = "动态子命令", RawName = child.Name,
                    Detail = "此命令由父组件动态生成，当前仅查看；如需调整请使用所属软件设置。" });
        }
        return output;
    }
    private static async Task<Result?> Probe(MenuEntry entry, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "--menu-probe", entry.Clsid, entry.Verb }) start.ArgumentList.Add(value);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(3500);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); return null; }
            await error;
            var text = await output;
            return process.ExitCode == 0 && text.Length <= 65536 ? JsonSerializer.Deserialize<Result>(text) : null;
        }
        catch { return null; }
    }
    internal static int RunHelper(string[] args)
    {
        if (args.Length != 3 || !Guid.TryParse(args[1], out var clsid) || args[2].Length > 512) return 2;
        object? instance = null;
        try
        {
            var iid = typeof(IExplorerCommand).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out var pointer));
            try { instance = Marshal.GetObjectForIUnknown(pointer); } finally { Marshal.Release(pointer); }
            if (instance is IInitializeCommand initializer && args[2].Length > 0) initializer.Initialize(args[2], IntPtr.Zero);
            var command = (IExplorerCommand)instance;
            var title = Title(command);
            if (string.IsNullOrWhiteSpace(title)) return 1;
            var children = new List<Child>();
            if (command.EnumSubCommands(out var items) >= 0 && items is not null)
            {
                try
                {
                    for (var i = 0; i < 24 && items.Next(1, out var child, out var count) >= 0 && count == 1; i++)
                    {
                        try { var name = child.GetCanonicalName(out var id) >= 0 && id != Guid.Empty ? id.ToString("D") : i.ToString(); var text = Title(child); if (!string.IsNullOrWhiteSpace(text)) children.Add(new(text, name + ":" + i)); }
                        finally { Marshal.ReleaseComObject(child); }
                    }
                }
                finally { Marshal.ReleaseComObject(items); }
            }
            Console.Write(JsonSerializer.Serialize(new Result(title, children.ToArray()))); return 0;
        }
        catch { return 1; }
        finally { if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
    }
    private static string Title(IExplorerCommand command)
    {
        var result = command.GetTitle(IntPtr.Zero, out var value);
        try { return result >= 0 && value != IntPtr.Zero ? (Marshal.PtrToStringUni(value) ?? "").Replace("&", "") : ""; }
        finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
    }
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IntPtr items, out IntPtr text);
        [PreserveSig] int GetIcon(IntPtr items, out IntPtr icon);
        [PreserveSig] int GetToolTip(IntPtr items, out IntPtr tip);
        [PreserveSig] int GetCanonicalName(out Guid name);
        [PreserveSig] int GetState(IntPtr items, [MarshalAs(UnmanagedType.Bool)] bool slow, out uint state);
        [PreserveSig] int Invoke(IntPtr items, IntPtr context);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands([MarshalAs(UnmanagedType.Interface)] out IEnumExplorerCommand? commands);
    }
    [ComImport, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumExplorerCommand
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IExplorerCommand command, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumExplorerCommand commands);
    }
    [ComImport, Guid("85075acf-231f-40ea-9610-d26b7b58f638"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeCommand
    { [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr properties); }
}
