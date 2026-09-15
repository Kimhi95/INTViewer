using System.IO;
using System.Text.Json;
using INTViewer.Models;

namespace INTViewer.Services;

public static class ViewerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static async Task<ViewerSettings>LoadAsync()
    {
        if (!File.Exists(SettingsFilePath)) return ViewerSettings.Default;

        try
        {
            await using FileStream stream = File.OpenRead(SettingsFilePath);
            return await JsonSerializer.DeserializeAsync<ViewerSettings>(stream, JsonOptions)
                ?? ViewerSettings.Default;
        }
        catch (IOException) { return ViewerSettings.Default; }
        catch (UnauthorizedAccessException) { return ViewerSettings.Default; }
        catch (JsonException) { return ViewerSettings.Default; }
    }

    public static async Task SaveAsync(ViewerSettings settings)
    {
        string temporaryPath = SettingsFilePath + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        }
        File.Move(temporaryPath, SettingsFilePath, overwrite: true);
    }

    public static void Save(ViewerSettings settings)
    {
        string temporaryPath = SettingsFilePath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsFilePath, overwrite: true);
    }
}
