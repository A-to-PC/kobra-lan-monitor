using System.Text.Json;
using System.Text.Json.Serialization;

namespace KobraLanMonitor;

public record PrinterProfile(string Id, string Name, string Host);

public class AppSettings
{
    // A user can have more than one Anycubic printer -- each gets its own saved profile here.
    // Only one is ever actively monitored/connected at a time (see ActivePrinterId), not all
    // simultaneously -- switching is a deliberate reconnect, not N parallel MQTT sessions.
    public List<PrinterProfile> Printers { get; set; } = new();
    public string? ActivePrinterId { get; set; }

    public string? AuthUsername { get; set; }
    public string? AuthPasswordHash { get; set; }

    // Last check-for-updates result, saved locally so it survives app restarts and doesn't need
    // a fresh (possibly cloud-dependent, possibly slow) query every time the popup is opened --
    // raw JSON rather than a typed shape since the real response format isn't confirmed yet.
    public string? LastFirmwareCheckJson { get; set; }
    public DateTimeOffset? LastFirmwareCheckAt { get; set; }

    // %LOCALAPPDATA%\KobraLanMonitor\settings.json -- deliberately NOT next to the exe.
    // Once the installer requires admin (for the Windows Firewall rule) and installs into
    // Program Files, a normal non-elevated process can't write there afterward. LocalAppData
    // is always writable by the current user regardless of where the exe itself lives, which
    // is the standard Windows convention for exactly this split (exe location vs per-user
    // mutable data) -- this also happens to resolve the Inno Setup "per-user area used with
    // admin privileges" warning at its root, rather than working around it in the installer.
    // KOBRA_LAN_MONITOR_DATA_DIR lets a second, isolated instance (e.g. for testing against the
    // K3M emulator) run with its own settings file instead of the shared per-user one -- without
    // it, this is unchanged from the normal %LOCALAPPDATA%\KobraLanMonitor\settings.json path.
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetEnvironmentVariable("KOBRA_LAN_MONITOR_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KobraLanMonitor"),
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // Migrate a pre-multi-printer settings.json (a single top-level "PrinterHost" string)
                // into the new Printers list -- existing installs must not lose their configured
                // printer just because this version added support for more than one.
                if (settings.Printers.Count == 0)
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("PrinterHost", out var legacyHost)
                        && legacyHost.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(legacyHost.GetString()))
                    {
                        var id = Guid.NewGuid().ToString("N");
                        settings.Printers.Add(new PrinterProfile(id, "Printer 1", legacyHost.GetString()!));
                        settings.ActivePrinterId = id;
                        settings.Save();
                    }
                }

                return settings;
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

    [JsonIgnore]
    public PrinterProfile? ActivePrinter => Printers.FirstOrDefault(p => p.Id == ActivePrinterId);

    [JsonIgnore]
    public bool IsConfigured => Printers.Count > 0
        && ActivePrinter != null
        && !string.IsNullOrEmpty(AuthUsername)
        && !string.IsNullOrEmpty(AuthPasswordHash);
}
