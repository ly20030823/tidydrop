namespace TidyDrop.App;

// Reading a menu must not activate its COM server. Even metadata-only COM calls can
// launch an application or show first-run UI. Keep unknown names and IDs intact.
internal static class MenuMetadata
{
    private static readonly Guid CopyPathClsid = new("62F68386-2117-4D94-9405-C9B792BE8EEB");

    public static List<MenuEntry> Read(IEnumerable<MenuEntry> entries)
        => entries.Select(entry => Guid.TryParse(entry.Clsid, out var clsid) && clsid == CopyPathClsid
            ? entry with { Title = "复制文件路径" }
            : entry).ToList();
}
