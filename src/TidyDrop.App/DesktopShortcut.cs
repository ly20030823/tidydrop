using System.IO;
using System.Runtime.InteropServices;

namespace TidyDrop.App;

internal static class DesktopShortcut
{
    public static void Create(string? folder = null)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var path = Path.Combine(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "归序.lnk");
            var executable = Environment.ProcessPath ?? throw new IOException("无法定位程序。");
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("无法创建 Windows 快捷方式。");
            shell = Activator.CreateInstance(shellType)!;
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(path);
            dynamic link = shortcut;
            if (File.Exists(path))
            {
                if (string.Equals((string)link.TargetPath, executable, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace((string)link.Arguments)) return;
                throw new IOException("桌面已有一个指向其他位置的「归序」快捷方式。请移走旧快捷方式后，再创建新的入口。");
            }
            link.TargetPath = executable;
            link.WorkingDirectory = AppContext.BaseDirectory;
            link.Description = "把文件拖到这里，按类型自动整理到指定文件夹";
            link.IconLocation = executable + ",0";
            link.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
