using System.Text.Json;

namespace KobraLanMonitor;

public class AppSettings
{
    public string? PrinterHost { get; set; }
    public string? AuthUsername { get; set; }
    public string? AuthPasswordHash { get; set; }

    // %LOCALAPPDATA%\KobraLanMonitor\settings.json -- deliberately NOT next to the exe.
    // Once the installer requires admin (for the Windows Firewall rule) and installs into
    // Program Files, a normal non-elevated process can't write there afterward. LocalAppData
    // is always writable by the current user regardless of where the exe itself lives, which
    // is the standard Windows convention for exactly this split (exe location vs per-user
    // mutable data) -- this also happens to resolve the Inno Setup "per-user area used with
    // admin privileges" warning at its root, rather than working around it in the installer.
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KobraLanMonitor", "settings.json");

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
