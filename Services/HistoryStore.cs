using System.IO;
using System.Text.Json;
using INTViewer.Models;

namespace INTViewer.Services;

public static class HistoryStore
{
    private const int MaxEntries = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string HistoryFilePath = Path.Combine(AppContext.BaseDirectory, "history.json");
    private static readonly SemaphoreSlim FileGate = new(1, 1);

    public static async Task<IReadOnlyList<RecentFileEntry>>LoadAsync()
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try { return await LoadCoreAsync().ConfigureAwait(false); }
        finally { FileGate.Release(); }
    }

    private static async Task<List<RecentFileEntry>> LoadCoreAsync()
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
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<RecentFileEntry> entries = await LoadCoreAsync().ConfigureAwait(false);
            entries.RemoveAll(item => string.Equals(item.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, entry);

            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            await SaveCoreAsync(entries).ConfigureAwait(false);
            return entries;
        }
        finally { FileGate.Release(); }
    }

    public static async Task<IReadOnlyList<RecentFileEntry>>RemoveAsync(string filePath)
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<RecentFileEntry> entries = await LoadCoreAsync().ConfigureAwait(false);
            entries.RemoveAll(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            await SaveCoreAsync(entries).ConfigureAwait(false);
            return entries;
        }
        finally { FileGate.Release(); }
    }

    public static async Task<IReadOnlyList<RecentFileEntry>>UpdateProgressAsync(string filePath, double progress)
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<RecentFileEntry> entries = await LoadCoreAsync().ConfigureAwait(false);
            int index = entries.FindIndex(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return entries;

            entries[index] = entries[index] with { ReadProgress = Math.Clamp(progress, 0, 100) };
            await SaveCoreAsync(entries).ConfigureAwait(false);
            return entries;
        }
        finally { FileGate.Release(); }
    }

    public static async Task ClearAsync()
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try { await SaveCoreAsync([]).ConfigureAwait(false); }
        finally { FileGate.Release(); }
    }

    private static async Task SaveCoreAsync(IReadOnlyList<RecentFileEntry> entries)
    {
        string temporaryPath = HistoryFilePath + ".tmp";

        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions).ConfigureAwait(false);
        }

        File.Move(temporaryPath, HistoryFilePath, overwrite: true);
    }
}
