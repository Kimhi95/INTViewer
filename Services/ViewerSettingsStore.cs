using System.IO;
using System.Text.Json;
using INTViewer.Models;

namespace INTViewer.Services;

public static class ViewerSettingsStore
{
    private const int MaximumSaveAttempts = 6;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly Semaphore ProcessGate = new(1, 1, @"Local\INTViewer.ViewerSettingsStore");

    public static async Task<ViewerSettings>LoadAsync()
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsFilePath)) return ViewerSettings.Default;
            await using FileStream stream = File.OpenRead(SettingsFilePath);
            return await JsonSerializer.DeserializeAsync<ViewerSettings>(stream, JsonOptions).ConfigureAwait(false)
                ?? ViewerSettings.Default;
        }
        catch (IOException) { return ViewerSettings.Default; }
        catch (UnauthorizedAccessException) { return ViewerSettings.Default; }
        catch (JsonException)
        {
            string backupPath = SettingsFilePath + ".bak";
            if (!File.Exists(backupPath)) return ViewerSettings.Default;
            try
            {
                File.Copy(SettingsFilePath, SettingsFilePath + ".corrupt", overwrite: true);
                File.Copy(backupPath, SettingsFilePath, overwrite: true);
                await using FileStream backup = File.OpenRead(backupPath);
                return await JsonSerializer.DeserializeAsync<ViewerSettings>(backup, JsonOptions).ConfigureAwait(false) ?? ViewerSettings.Default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ViewerSettings.Default; }
        }
        finally { FileGate.Release(); }
    }

    public static async Task SaveAsync(ViewerSettings settings)
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        string temporaryPath = CreateTemporaryPath();
        bool processGateEntered = false;
        try
        {
            processGateEntered = WaitForProcessGate();
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            await ReplaceSettingsFileWithRetryAsync(temporaryPath).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporaryPath);
            if (processGateEntered) ProcessGate.Release();
            FileGate.Release();
        }
    }

    public static void Save(ViewerSettings settings)
    {
        FileGate.Wait();
        string temporaryPath = CreateTemporaryPath();
        bool processGateEntered = false;
        try
        {
            processGateEntered = WaitForProcessGate();
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            ReplaceSettingsFileWithRetry(temporaryPath);
        }
        finally
        {
            TryDelete(temporaryPath);
            if (processGateEntered) ProcessGate.Release();
            FileGate.Release();
        }
    }

    private static string CreateTemporaryPath() =>
        $"{SettingsFilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    private static bool WaitForProcessGate()
    {
        if (ProcessGate.WaitOne(TimeSpan.FromSeconds(5))) return true;
        throw new IOException("다른 INTViewer 창에서 설정 저장을 완료하지 못했습니다.");
    }

    private static async Task ReplaceSettingsFileWithRetryAsync(string temporaryPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                ReplaceSettingsFile(temporaryPath);
                return;
            }
            catch (IOException) when (attempt < MaximumSaveAttempts - 1)
            {
                await Task.Delay(GetRetryDelay(attempt)).ConfigureAwait(false);
            }
        }
    }

    private static void ReplaceSettingsFileWithRetry(string temporaryPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                ReplaceSettingsFile(temporaryPath);
                return;
            }
            catch (IOException) when (attempt < MaximumSaveAttempts - 1)
            {
                Thread.Sleep(GetRetryDelay(attempt));
            }
        }
    }

    private static void ReplaceSettingsFile(string temporaryPath)
    {
        if (File.Exists(SettingsFilePath))
        {
            File.Replace(temporaryPath, SettingsFilePath, SettingsFilePath + ".bak", ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, SettingsFilePath);
    }

    private static TimeSpan GetRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(40 * (1 << attempt));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
