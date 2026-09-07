using System.Text.Json;

namespace KobraLanMonitor;

public class AppSettings
{
    public string? PrinterHost { get; set; }
    public string? AuthUsername { get; set; }
    public string? AuthPasswordHash { get; set; }

    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall through to defaults if the file is missing or corrupt.
        }
        return new AppSettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public bool IsConfigured => !string.IsNullOrEmpty(PrinterHost)
        && !string.IsNullOrEmpty(AuthUsername)
        && !string.IsNullOrEmpty(AuthPasswordHash);
}
