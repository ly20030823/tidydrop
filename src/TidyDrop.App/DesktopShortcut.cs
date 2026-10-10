using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TidyDrop.App;

internal static class DesktopShortcut
{
    public static void Create(string? folder = null)
    {
        // IShellLinkW and IPersistFile preserve Chinese paths on any Windows display language.
        // https://learn.microsoft.com/windows/win32/shell/links
        var path = Path.Combine(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "归序.lnk");
        var executable = Environment.ProcessPath ?? throw new IOException("无法定位程序。");
        var linkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
        var shellLink = Activator.CreateInstance(linkType!) ?? throw new IOException("无法创建 Windows 快捷方式。");
        try
        {
            var link = (IShellLinkW)shellLink;
            var file = (IPersistFile)shellLink;
            if (File.Exists(path))
            {
                file.Load(path, 0);
                var target = new StringBuilder(32768);
                var arguments = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                link.GetArguments(arguments, arguments.Capacity);
                if (target.ToString().Equals(executable, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(arguments.ToString())) return;
                throw new IOException("桌面已有一个指向其他位置的「归序」快捷方式。请移走旧快捷方式后，再创建新的入口。");
            }
            link.SetPath(executable);
            link.SetWorkingDirectory(AppContext.BaseDirectory);
            link.SetDescription("拖入文件或文件夹，后台收纳；双击查看归档空间");
            link.SetIconLocation(executable, 0);
            file.Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(shellLink); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemList);
        void SetIDList(IntPtr itemList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
