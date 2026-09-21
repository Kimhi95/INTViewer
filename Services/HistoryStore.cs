using System.IO;
using System.Text.Json;
using INTViewer.Models;

namespace INTViewer.Services;

public static class HistoryStore
{
    private const int MaxEntries = 100;
    private const int MaximumIoAttempts = 6;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string HistoryFilePath = Path.Combine(AppContext.BaseDirectory, "history.json");
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly Semaphore ProcessGate = new(1, 1, @"Local\INTViewer.HistoryStore");

    public static Task<IReadOnlyList<RecentFileEntry>> LoadAsync() => ExecuteAsync(async () =>
        (IReadOnlyList<RecentFileEntry>)await LoadCoreAsync().ConfigureAwait(false));

    public static Task<IReadOnlyList<RecentFileEntry>> AddOrUpdateAsync(RecentFileEntry entry) => ExecuteAsync<IReadOnlyList<RecentFileEntry>>(async () =>
    {
        List<RecentFileEntry> entries = await LoadCoreAsync().ConfigureAwait(false);
        entries.RemoveAll(item => string.Equals(item.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, Sanitize(entry));
        entries = Normalize(entries);
        await SaveCoreAsync(entries).ConfigureAwait(false);
        return entries;
    });

    public static Task<IReadOnlyList<RecentFileEntry>> RemoveAsync(string filePath) => ExecuteAsync<IReadOnlyList<RecentFileEntry>>(async () =>
    {
        List<RecentFileEntry> entries = await LoadCoreAsync().ConfigureAwait(false);
        entries.RemoveAll(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        await SaveCoreAsync(entries).ConfigureAwait(false);
        return entries;
    });

    public static Task<IReadOnlyList<RecentFileEntry>> UpdateProgressAsync(string filePath, double progress)
        => UpdateReadingStateAsync(filePath, progress, null, null);

    public static Task<IReadOnlyList<RecentFileEntry>> UpdateReadingStateAsync(
        string filePath, double progress, int? readPosition, int[]? bookmarks) => ExecuteAsync<IReadOnlyList<RecentFileEntry>>(async () =>
    {
        List<RecentFileEntry> entries = await LoadCoreAsync().ConfigureAwait(false);
        int index = entries.FindIndex(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return entries;

        RecentFileEntry current = entries[index];
        entries[index] = Sanitize(current with
        {
            ReadProgress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 100) : current.ReadProgress,
            ReadPosition = readPosition.HasValue ? Math.Max(0, readPosition.Value) : current.ReadPosition,
            BookmarkPositions = bookmarks ?? current.BookmarkPositions
        });
        await SaveCoreAsync(entries).ConfigureAwait(false);
        return entries;
    });

    public static Task<IReadOnlyList<RecentFileEntry>> SetPinnedAsync(string filePath, bool isPinned) => ExecuteAsync<IReadOnlyList<RecentFileEntry>>(async () =>
    {
        List<RecentFileEntry> entries = await LoadCoreAsync().ConfigureAwait(false);
        int index = entries.FindIndex(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) entries[index] = entries[index] with { IsPinned = isPinned };
        entries = entries.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.LastOpenedAt).ToList();
        await SaveCoreAsync(entries).ConfigureAwait(false);
        return entries;
    });

    public static Task ClearAsync() => ExecuteAsync(() => SaveCoreAsync([]));

    private static async Task<List<RecentFileEntry>> LoadCoreAsync()
    {
        if (!File.Exists(HistoryFilePath)) return [];

        try
        {
            List<RecentFileEntry?>? loaded = await ReadJsonWithRetryAsync(HistoryFilePath).ConfigureAwait(false);
            return Normalize(loaded ?? []);
        }
        catch (JsonException)
        {
            string backupPath = HistoryFilePath + ".bak";
            TryPreserveCorruptFile(HistoryFilePath);
            if (!File.Exists(backupPath)) return [];
            try
            {
                List<RecentFileEntry?>? restored = await ReadJsonWithRetryAsync(backupPath).ConfigureAwait(false);
                File.Copy(backupPath, HistoryFilePath, overwrite: true);
                return Normalize(restored ?? []);
            }
            catch (JsonException) { return []; }
        }
    }

    private static async Task<List<RecentFileEntry?>?> ReadJsonWithRetryAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, useAsync: true);
                return await JsonSerializer.DeserializeAsync<List<RecentFileEntry?>>(stream, JsonOptions).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < MaximumIoAttempts - 1)
            {
                await Task.Delay(GetRetryDelay(attempt)).ConfigureAwait(false);
            }
        }
    }

    private static async Task SaveCoreAsync(IReadOnlyList<RecentFileEntry> entries)
    {
        string temporaryPath = $"{HistoryFilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, entries, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(HistoryFilePath))
                        File.Replace(temporaryPath, HistoryFilePath, HistoryFilePath + ".bak", ignoreMetadataErrors: true);
                    else
                        File.Move(temporaryPath, HistoryFilePath);
                    return;
                }
                catch (IOException) when (attempt < MaximumIoAttempts - 1)
                {
                    await Task.Delay(GetRetryDelay(attempt)).ConfigureAwait(false);
                }
            }
        }
        finally { TryDelete(temporaryPath); }
    }

    private static List<RecentFileEntry> Normalize(IEnumerable<RecentFileEntry?> entries) => entries
        .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.FilePath))
        .Select(entry => Sanitize(entry!))
        .GroupBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(entry => entry.LastOpenedAt).First())
        .OrderByDescending(entry => entry.IsPinned)
        .ThenByDescending(entry => entry.LastOpenedAt)
        .Take(MaxEntries)
        .ToList();

    private static RecentFileEntry Sanitize(RecentFileEntry entry) => entry with
    {
        EncodingName = string.IsNullOrWhiteSpace(entry.EncodingName) ? "알 수 없음" : entry.EncodingName,
        ByteLength = Math.Max(0, entry.ByteLength),
        ReadProgress = double.IsFinite(entry.ReadProgress) ? Math.Clamp(entry.ReadProgress, 0, 100) : 0,
        ReadPosition = Math.Max(0, entry.ReadPosition),
        BookmarkPositions = (entry.BookmarkPositions ?? []).Where(position => position >= 0).Distinct()
            .OrderBy(position => position).Take(1000).ToArray()
    };

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        bool processGateEntered = false;
        try
        {
            processGateEntered = await Task.Run(WaitForProcessGate).ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        }
        finally
        {
            if (processGateEntered) ProcessGate.Release();
            FileGate.Release();
        }
    }

    private static async Task ExecuteAsync(Func<Task> action)
    {
        await ExecuteAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static bool WaitForProcessGate()
    {
        if (ProcessGate.WaitOne(TimeSpan.FromSeconds(5))) return true;
        throw new IOException("다른 INTViewer 창에서 최근 기록 저장을 완료하지 못했습니다.");
    }

    private static TimeSpan GetRetryDelay(int attempt) => TimeSpan.FromMilliseconds(40 * (1 << attempt));

    private static void TryPreserveCorruptFile(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
