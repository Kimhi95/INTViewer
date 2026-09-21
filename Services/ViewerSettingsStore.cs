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
        bool processGateEntered = false;
        try
        {
            processGateEntered = await Task.Run(WaitForProcessGate).ConfigureAwait(false);
            if (!File.Exists(SettingsFilePath)) return ViewerSettings.Default;
            ViewerSettings? settings = await ReadSettingsWithRetryAsync(SettingsFilePath).ConfigureAwait(false);
            return Normalize(settings ?? ViewerSettings.Default);
        }
        catch (JsonException)
        {
            string backupPath = SettingsFilePath + ".bak";
            if (!File.Exists(backupPath)) return ViewerSettings.Default;
            try
            {
                File.Copy(SettingsFilePath, SettingsFilePath + ".corrupt", overwrite: true);
                File.Copy(backupPath, SettingsFilePath, overwrite: true);
                ViewerSettings? restored = await ReadSettingsWithRetryAsync(backupPath).ConfigureAwait(false);
                return Normalize(restored ?? ViewerSettings.Default);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ViewerSettings.Default; }
        }
        finally
        {
            if (processGateEntered) ProcessGate.Release();
            FileGate.Release();
        }
    }

    public static async Task SaveAsync(ViewerSettings settings)
    {
        settings = Normalize(settings);
        await FileGate.WaitAsync().ConfigureAwait(false);
        string temporaryPath = CreateTemporaryPath();
        bool processGateEntered = false;
        try
        {
            processGateEntered = await Task.Run(WaitForProcessGate).ConfigureAwait(false);
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
        settings = Normalize(settings);
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

    private static async Task<ViewerSettings?> ReadSettingsWithRetryAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
                return await JsonSerializer.DeserializeAsync<ViewerSettings>(stream, JsonOptions).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < MaximumSaveAttempts - 1)
            {
                await Task.Delay(GetRetryDelay(attempt)).ConfigureAwait(false);
            }
        }
    }

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

    private static ViewerSettings Normalize(ViewerSettings settings)
    {
        ViewerSettings defaults = ViewerSettings.Default;
        if (settings.SettingsVersion < 2)
            settings = settings with { MarginBottom = defaults.MarginBottom };
        if (settings.SettingsVersion < 3)
            settings = settings with { FontFamily = defaults.FontFamily, Bold = false, Italic = false };
        if (settings.SettingsVersion < 4)
            settings = settings with { LineSpacing = defaults.LineSpacing };
        return settings with
        {
            BackgroundColor = IsRgbColor(settings.BackgroundColor) ? settings.BackgroundColor : defaults.BackgroundColor,
            TextColor = IsRgbColor(settings.TextColor) ? settings.TextColor : defaults.TextColor,
            WindowWidth = ClampFinite(settings.WindowWidth, 360, 7680, defaults.WindowWidth),
            WindowHeight = ClampFinite(settings.WindowHeight, 480, 4320, defaults.WindowHeight),
            MarginTop = ClampFinite(settings.MarginTop, 0, 120, defaults.MarginTop),
            MarginBottom = ClampFinite(settings.MarginBottom, 0, 120, defaults.MarginBottom),
            MarginLeft = ClampFinite(settings.MarginLeft, 0, 120, defaults.MarginLeft),
            MarginRight = ClampFinite(settings.MarginRight, 0, 120, defaults.MarginRight),
            FontSize = ClampFinite(settings.FontSize, 10, 36, defaults.FontSize),
            FontFamily = string.IsNullOrWhiteSpace(settings.FontFamily) || settings.FontFamily.Length > 200
                ? defaults.FontFamily
                : settings.FontFamily.Trim(),
            LineSpacing = ClampFinite(settings.LineSpacing, 1, 2.2, defaults.LineSpacing),
            SettingsVersion = defaults.SettingsVersion
        };
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static bool IsRgbColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
