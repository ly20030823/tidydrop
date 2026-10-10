using System.Security.Cryptography;
using System.Text;

namespace TidyDrop.Core;

internal static class DirectoryTransfer
{
    private sealed record Tree(string[] Directories, string[] Files);

    private static Tree ReadTree(string root)
    {
        var directories = new List<string>();
        var files = new List<string>();
        Visit(root);
        return new(directories.Order(StringComparer.Ordinal).ToArray(), files.Order(StringComparer.Ordinal).ToArray());
        void Visit(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("文件夹包含链接或云端占位项，请先将其下载为本地文件。");
            directories.Add(Path.GetRelativePath(root, directory));
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("文件夹包含链接或云端占位项，请先将其下载为本地文件。");
                if ((attributes & FileAttributes.Directory) != 0) Visit(child);
                else files.Add(Path.GetRelativePath(root, child));
            }
        }
    }

    public static void Validate(string root) => ReadTree(root);

    public static string Hash(string root)
    {
        var tree = ReadTree(root);
        return Fingerprint(tree, name => { using var input = File.OpenRead(Path.Combine(root, name)); return SHA256.HashData(input); });
    }

    private static string Fingerprint(Tree tree, Func<string, byte[]> hashFile)
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        {
            foreach (var directory in tree.Directories) { writer.Write((byte)0); writer.Write(directory); }
            foreach (var file in tree.Files) { writer.Write((byte)1); writer.Write(file); writer.Write(hashFile(file)); }
        }
        return Convert.ToHexString(SHA256.HashData(output.ToArray()));
    }

    public static void Move(string source, string destination, string? expectedHash, Action<string> beforeMove)
    {
        var tree = ReadTree(source);
        var handles = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            foreach (var file in tree.Files)
                handles.Add(file, new FileStream(Path.Combine(source, file), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            var hash = Fingerprint(tree, name => SHA256.HashData(handles[name]));
            if (expectedHash is not null && hash != expectedHash)
                throw new IOException("文件夹内容在归档后已被修改，撤销已停止。");
            if (Hash(source) != hash) throw new IOException("文件夹正在变化，原文件夹已保留，请稍后重试。");
            beforeMove(hash);
            var parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            if (Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            {
                // Windows refuses a directory rename while descendants have open handles.
                // Release after verification; the directory rename itself preserves every child.
                foreach (var handle in handles.Values) handle.Dispose();
                handles.Clear();
                Directory.Move(source, destination);
                return;
            }

            // Keep a verified copy if any source deletion fails; never recursively delete unknown files.
            var temporary = Path.Combine(parent, ".tidydrop-" + Guid.NewGuid().ToString("N") + ".tmp");
            Directory.CreateDirectory(temporary);
            foreach (var directory in tree.Directories) Directory.CreateDirectory(Path.Combine(temporary, directory));
            foreach (var file in tree.Files)
            {
                var input = handles[file];
                input.Position = 0;
                var copied = Path.Combine(temporary, file);
                using (var output = new FileStream(copied, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(true); }
                File.SetCreationTimeUtc(copied, File.GetCreationTimeUtc(Path.Combine(source, file)));
                File.SetLastWriteTimeUtc(copied, File.GetLastWriteTimeUtc(Path.Combine(source, file)));
            }
            foreach (var directory in tree.Directories.Reverse())
            {
                Directory.SetCreationTimeUtc(Path.Combine(temporary, directory), Directory.GetCreationTimeUtc(Path.Combine(source, directory)));
                Directory.SetLastWriteTimeUtc(Path.Combine(temporary, directory), Directory.GetLastWriteTimeUtc(Path.Combine(source, directory)));
            }
            if (Hash(temporary) != hash || Hash(source) != hash)
                throw new IOException("文件夹复制校验失败或源内容发生变化，原文件夹与临时副本已保留。");
            Directory.Move(temporary, destination);
            // The copy is complete. Only remove the captured files and then empty directories.
            foreach (var file in tree.Files) File.Delete(Path.Combine(source, file));
            foreach (var directory in tree.Directories.Reverse()) Directory.Delete(Path.Combine(source, directory), false);
        }
        finally { foreach (var handle in handles.Values) handle.Dispose(); }
    }
}
