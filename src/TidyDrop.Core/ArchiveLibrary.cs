using System.Globalization;

namespace TidyDrop.Core;

public static class ArchiveLibrary
{
    public static List<ArchiveItem> Read(Settings settings, IEnumerable<HistoryEntry> history)
    {
        var result = new Dictionary<string, ArchiveItem>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(settings.RootFolder)) return [];
        var root = Path.GetFullPath(settings.RootFolder);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        // Legacy entries remain searchable without moving existing user materials.
        foreach (var entry in history.Where(e => e.State == "已归档" && e.Destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            if (Organizer.Exists(entry.Destination))
                result[entry.Destination] = new(entry.Id, Path.GetFileName(entry.Destination), entry.Destination, entry.RuleName, entry.Time.ToLocalTime().ToString("yyyy-MM-dd"), Directory.Exists(entry.Destination));
        var managed = Path.Combine(root, "归序");
        if (!Directory.Exists(managed) || IsLink(managed)) return result.Values.OrderByDescending(x => x.Date).ToList();
        foreach (var month in Directory.EnumerateDirectories(managed).Where(p => Path.GetFileName(p) != ArchiveCatalog.MetadataFolder && !IsLink(p)))
        foreach (var day in Directory.EnumerateDirectories(month).Where(p => !IsLink(p)))
        {
            var date = Path.GetFileName(day);
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
            foreach (var category in Directory.EnumerateDirectories(day).Where(p => !IsLink(p)))
            foreach (var path in Directory.EnumerateFileSystemEntries(category))
            {
                if (Path.GetFileName(path).StartsWith(".tidydrop-", StringComparison.OrdinalIgnoreCase) || IsLink(path)) continue;
                result[path] = new(result.GetValueOrDefault(path)?.Id ?? path, Path.GetFileName(path), path, Path.GetFileName(category), date, Directory.Exists(path));
            }
        }
        return result.Values.OrderByDescending(x => x.Date).ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
