using System.IO;
using System.Runtime.InteropServices;

namespace TidyDrop.App;

// Used only by the optional interactive smoke check, with generated fixtures.
internal static class ExplorerSelectionCheck
{
    public static async Task VerifyAsync(string path, Func<Task> click)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic windows = shell.Windows();
        var existingHandles = new HashSet<long>();
        try
        {
            for (var i = 0; i < (int)windows.Count; i++)
            {
                dynamic window = windows.Item(i);
                try { existingHandles.Add((long)window.HWND); }
                finally { Marshal.ReleaseComObject(window); }
            }
            await click();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                for (var i = 0; i < (int)windows.Count; i++)
                {
                    dynamic window = windows.Item(i);
                    try
                    {
                        if (!Matches(window, path)) continue;
                        // Never close a window that was already open before this fixture check.
                        if (!existingHandles.Contains((long)window.HWND)) window.Quit();
                        return;
                    }
                    catch (COMException) { /* Explorer may still be navigating. */ }
                    finally { Marshal.ReleaseComObject(window); }
                }
                await Task.Delay(100);
            }
            throw new IOException("Explorer did not open the containing folder with the material selected: " + path);
        }
        finally
        {
            Marshal.ReleaseComObject(windows);
            Marshal.ReleaseComObject(shell);
        }
    }

    private static bool Matches(dynamic window, string path)
    {
        dynamic document = window.Document;
        try
        {
            dynamic folder = document.Folder;
            try
            {
                dynamic self = folder.Self;
                try
                {
                    if (!string.Equals((string)self.Path, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)) return false;
                }
                finally { Marshal.ReleaseComObject(self); }
            }
            finally { Marshal.ReleaseComObject(folder); }
            dynamic selected = document.SelectedItems();
            try
            {
                for (var i = 0; i < (int)selected.Count; i++)
                {
                    dynamic item = selected.Item(i);
                    try { if (string.Equals((string)item.Path, path, StringComparison.OrdinalIgnoreCase)) return true; }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return false;
            }
            finally { Marshal.ReleaseComObject(selected); }
        }
        finally { Marshal.ReleaseComObject(document); }
    }
}
