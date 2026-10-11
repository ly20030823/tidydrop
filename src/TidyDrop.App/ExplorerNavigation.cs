using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TidyDrop.App;

internal static class ExplorerNavigation
{
    // Shell identifiers avoid Explorer command-line quoting problems with spaces and commas.
    public static Task RevealAsync(string path) => Task.Run(() =>
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("这份材料已被移走，请刷新归档空间。", fullPath);

        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        IntPtr item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(initialized);
            Marshal.ThrowExceptionForHR(SHParseDisplayName(fullPath, IntPtr.Zero, out item, 0, out _));
            // With a zero item count, Windows opens the parent and selects this absolute item.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        catch (COMException)
        {
            var parent = Path.GetDirectoryName(fullPath);
            if (parent is null || !Directory.Exists(parent)) throw;
            // Even if selection is unavailable, take the user to the actual containing directory.
            Process.Start(new ProcessStartInfo(parent) { UseShellExecute = true });
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
            if (initialized >= 0) CoUninitialize();
        }
    });

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint attributes, out uint actualAttributes);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
