namespace TidyDrop.Core;

public sealed class Settings
{
    public string RootFolder { get; set; } = "";
    // Retained only so old settings can be read. Storage now always uses the intake date.
    public bool DateFolders { get; set; }
    public bool AutoOrganize { get; set; } = true;
}

public sealed record PlanItem(string Source, string? Destination, string RuleName, string Message, bool IsDirectory = false)
{
    public string FileName => Path.GetFileName(Source);
    public bool CanMove => Destination is not null;
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Hash { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string State { get; set; } = "待执行";
    public string Detail { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string FileName => Path.GetFileName(Source);
    public string DisplayTime => Time.ToLocalTime().ToString("MM-dd HH:mm:ss");
}

public sealed record OperationResult(string FileName, bool Success, string Message);

public sealed record ArchiveItem(string Id, string FileName, string Path, string Category, string Date, bool IsDirectory);
