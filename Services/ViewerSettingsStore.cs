using System.IO;
using System.Text.Json;
using INTViewer.Models;

namespace INTViewer.Services;

public static class ViewerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly SemaphoreSlim FileGate = new(1, 1);

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
        catch (JsonException) { return ViewerSettings.Default; }
        finally { FileGate.Release(); }
    }

    public static async Task SaveAsync(ViewerSettings settings)
    {
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string temporaryPath = SettingsFilePath + ".tmp";
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions).ConfigureAwait(false);
            }
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
        }
        finally { FileGate.Release(); }
    }

    public static void Save(ViewerSettings settings)
    {
        FileGate.Wait();
        try
        {
            string temporaryPath = SettingsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
        }
        finally { FileGate.Release(); }
    }
}
