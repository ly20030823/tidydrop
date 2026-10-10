using System.Text.Json;

namespace TidyDrop.Core;

public sealed class Store
{
    private readonly string directory;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public Store(string directory) => this.directory = Path.GetFullPath(directory);
    public Settings LoadSettings() => Read<Settings>("settings.json") ?? new();
    public void SaveSettings(Settings settings) => Write("settings.json", settings);
    public List<HistoryEntry> LoadHistory() => Read<List<HistoryEntry>>("history.json") ?? [];
    public void SaveHistory(IEnumerable<HistoryEntry> history) => Write("history.json", history.ToList());

    private T? Read<T>(string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"无法读取 {name}，请保留文件并检查内容。");
    }

    private void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
}
