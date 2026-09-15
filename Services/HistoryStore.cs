using System.IO;
using System.Text.Json;
using INTViewer.Models;

namespace INTViewer.Services;

public static class HistoryStore
{
    private const int MaxEntries = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string HistoryFilePath = Path.Combine(AppContext.BaseDirectory, "history.json");

    public static async Task<IReadOnlyList<RecentFileEntry>>LoadAsync()
    {
        if (!File.Exists(HistoryFilePath))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(HistoryFilePath);
            return await JsonSerializer.DeserializeAsync<List<RecentFileEntry>>(stream, JsonOptions) ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task<IReadOnlyList<RecentFileEntry>>AddOrUpdateAsync(RecentFileEntry entry)
    {
        List<RecentFileEntry> entries = (await LoadAsync()).ToList();
        entries.RemoveAll(item => string.Equals(item.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, entry);

        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        await SaveAsync(entries);
        return entries;
    }

    public static async Task<IReadOnlyList<RecentFileEntry>>RemoveAsync(string filePath)
    {
        List<RecentFileEntry> entries = (await LoadAsync()).ToList();
        entries.RemoveAll(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(entries);
        return entries;
    }

    public static async Task<IReadOnlyList<RecentFileEntry>>UpdateProgressAsync(string filePath, double progress)
    {
        List<RecentFileEntry> entries = (await LoadAsync()).ToList();
        int index = entries.FindIndex(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return entries;

        entries[index] = entries[index] with { ReadProgress = Math.Clamp(progress, 0, 100) };
        await SaveAsync(entries);
        return entries;
    }

    public static Task ClearAsync() => SaveAsync([]);

    private static async Task SaveAsync(IReadOnlyList<RecentFileEntry> entries)
    {
        string temporaryPath = HistoryFilePath + ".tmp";

        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions);
        }

        File.Move(temporaryPath, HistoryFilePath, overwrite: true);
    }
}
