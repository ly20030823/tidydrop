using System.IO;
using System.Runtime.InteropServices;

namespace TidyDrop.App;

internal static class RecycleBin
{
    internal sealed record Receipt(string OriginalPath, string RecycledPath);
    private const uint RecycleOnlyFlags = 0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x0004 | 0x0010 | 0x4000 | 0x2000;

    // IFileOperation is run on a dedicated STA. No shell delete command or permanent-delete API is used.
    internal static Task<T> RunAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static Receipt Move(string path)
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 2);
        IFileOperation? operation = null;
        IShellItem? item = null;
        var sink = new RecycleProgress();
        try
        {
            Marshal.ThrowExceptionForHR(initialized);
            operation = CreateOperation();
            item = CreateItem(Path.GetFullPath(path));
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(RecycleOnlyFlags));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
            var result = operation.PerformOperations();
            if (sink.RefusedPermanentDelete) throw new IOException("这个位置或项目无法移入回收站，材料已保留。可以通过「打开位置」手动处理。");
            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted || !sink.Completed) throw new IOException("移入回收站未完成，材料未从清单移除。");
            Marshal.ThrowExceptionForHR(sink.Result);
            if (string.IsNullOrEmpty(sink.RecycledPath)) throw new IOException("Windows 未返回回收结果，请核对回收站并刷新材料清单。");
            return new(path, sink.RecycledPath);
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (operation is not null) Marshal.ReleaseComObject(operation);
            if (initialized >= 0) CoUninitialize();
            GC.KeepAlive(sink);
        }
    }

    // Used only by isolated integration checks to return their own generated fixtures from the bin.
    internal static void RestoreForCheck(Receipt receipt)
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 2);
        IFileOperation? operation = null;
        IShellItem? source = null;
        IShellItem? folder = null;
        try
        {
            Marshal.ThrowExceptionForHR(initialized);
            if (File.Exists(receipt.OriginalPath) || Directory.Exists(receipt.OriginalPath)) throw new IOException("Restore would overwrite a fixture.");
            operation = CreateOperation(); source = CreateItem(receipt.RecycledPath);
            folder = CreateItem(Path.GetDirectoryName(receipt.OriginalPath)!);
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x00100000 | 0x0400 | 0x0004));
            Marshal.ThrowExceptionForHR(operation.MoveItem(source, folder, Path.GetFileName(receipt.OriginalPath), IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted) throw new IOException("Fixture restore was aborted.");
        }
        finally
        {
            if (source is not null) Marshal.ReleaseComObject(source);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (operation is not null) Marshal.ReleaseComObject(operation);
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static IFileOperation CreateOperation() => (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
    private static IShellItem CreateItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        return item;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class RecycleProgress : IFileOperationProgressSink
    {
        internal bool RefusedPermanentDelete;
        internal bool Completed;
        internal int Result;
        internal string RecycledPath = "";
        public int PreDeleteItem(uint flags, IntPtr item)
        {
            // Windows clears TSF_DELETE_RECYCLE_IF_POSSIBLE when it proposes permanent destruction.
            if ((flags & 0x80) != 0) return 0;
            RefusedPermanentDelete = true;
            return unchecked((int)0x80004004); // E_ABORT vetoes this item and remaining operations.
        }
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr recycled)
        {
            Completed = true; Result = result;
            if (result >= 0 && recycled != IntPtr.Zero)
            {
                var shellItem = (IShellItem)Marshal.GetObjectForIUnknown(recycled);
                try
                {
                    if (shellItem.GetDisplayName(0x80028000, out var name) >= 0)
                    { try { RecycledPath = Marshal.PtrToStringUni(name) ?? ""; } finally { Marshal.FreeCoTaskMem(name); } }
                }
                finally { Marshal.ReleaseComObject(shellItem); }
            }
            return 0;
        }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, string name) => 0;
        public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, string name) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, string name, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, string name) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, string name, int result, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr folder, string name) => 0;
        public int PostNewItem(uint flags, IntPtr folder, string name, string template, uint attributes, int result, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr recycled);
        [PreserveSig] int PreNewItem(uint flags, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr folder, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr window);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem folder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem folder);
        [PreserveSig] int DeleteItem(IShellItem item, [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
