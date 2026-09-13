using System.Text.Json;
using System.Text.Json.Serialization;

namespace KobraLanMonitor;

public class PrinterState
{
    public string ConnectionStatus { get; set; } = "connecting"; // connecting | connected | disconnected | error
    public string? Error { get; set; }
    public string? State { get; set; } // e.g. printing, paused, idle, done, failed
    public int? Progress { get; set; } // 0-100
    public int? CurrLayer { get; set; }
    public int? TotalLayers { get; set; }
    public int? RemainTimeSeconds { get; set; }
    public string? Filename { get; set; }
    public int? NozzleTemp { get; set; }
    public int? NozzleTargetTemp { get; set; }
    public int? BedTemp { get; set; }
    public int? BedTargetTemp { get; set; }
    public int? FanSpeedPct { get; set; }
    public bool? LightOn { get; set; }
    public int? LightBrightness { get; set; }
    public int? LightType { get; set; }
    public int? PrintSpeedMode { get; set; }
    public List<FilamentSlot>? FilamentSlots { get; set; }
    public int? BoxTemp { get; set; }
    public int? LoadedSlot { get; set; }
    public bool? DryingOn { get; set; }
    public int? DryingTargetTemp { get; set; }
    public string? PrinterVersion { get; set; }
    public string? AceProVersion { get; set; }

    // The real OTA check-for-updates exchange (confirmed 13/09/2026 via Rinkhals' own working
    // check_updates.py, github.com/jbatonnet/Rinkhals): gkapi auto-publishes reportVersion on
    // every cloud connect, and the server's actual answer arrives asynchronously on a plain
    // "ota" topic (no "/report" suffix) -- there is no synchronous query/response command to
    // call, so this is populated passively whenever such a message is overheard, never by an
    // active request. The real field shape of a genuine "update available" reply is unconfirmed
    // (Rinkhals' own reference script doesn't interpret it either, just surfaces it raw), so this
    // stays a raw JsonElement rather than typed fields until a real one is actually observed.
    public JsonElement? LatestOtaData { get; set; }
    public DateTimeOffset? LatestOtaAt { get; set; }

    public DateTimeOffset? LastUpdated { get; set; }

    [JsonIgnore]
    public string? FileUploadUrl { get; set; }

    [JsonIgnore]
    public Dictionary<string, JsonElement> RawByTopic { get; } = new();

    public Dictionary<string, JsonElement> Raw => RawByTopic;

    private readonly Lock _lock = new();

    /// <summary>
    /// Clears everything back to defaults when switching to a different saved printer -- without
    /// this, the dashboard would keep showing the previous printer's status/temps/filename for the
    /// brief window before the new connection's own reports start arriving.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            ConnectionStatus = "connecting";
            Error = null;
            State = null;
            Progress = null;
            CurrLayer = null;
            TotalLayers = null;
            RemainTimeSeconds = null;
            Filename = null;
            NozzleTemp = null;
            NozzleTargetTemp = null;
            BedTemp = null;
            BedTargetTemp = null;
            FanSpeedPct = null;
            LightOn = null;
            LightBrightness = null;
            LightType = null;
            PrintSpeedMode = null;
            FilamentSlots = null;
            BoxTemp = null;
            LoadedSlot = null;
            DryingOn = null;
            DryingTargetTemp = null;
            PrinterVersion = null;
            AceProVersion = null;
            LatestOtaData = null;
            LatestOtaAt = null;
            LastUpdated = null;
            FileUploadUrl = null;
            RawByTopic.Clear();
        }
    }

    public void ApplyMessage(string reportType, JsonElement payload)
    {
        lock (_lock)
        {
            RawByTopic[reportType] = payload.Clone();
            LastUpdated = DateTimeOffset.UtcNow;

            if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (reportType)
            {
                case "info":
                    // "urls.fileUploadurl" carries a per-session upload token needed for remote print --
                    // it's present alongside "project" regardless of whether a print is active.
                    if (data.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Object
                        && urls.TryGetProperty("fileUploadurl", out var fu) && fu.ValueKind == JsonValueKind.String)
                    {
                        FileUploadUrl = fu.GetString();
                    }

                    // Current firmware version, top-level on the info report per the documented Kobra 3
                    // MQTT command reference (rvanderp3/kobra-connect) -- not yet live-confirmed on this
                    // printer specifically.
                    if (data.TryGetProperty("version", out var pv) && pv.ValueKind == JsonValueKind.String)
                        PrinterVersion = pv.GetString();

                    if (data.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object)
                    {
                        if (project.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
                            State = s.GetString();
                        if (project.TryGetProperty("progress", out var p) && p.TryGetInt32(out var progress))
                            Progress = progress;
                        if (project.TryGetProperty("curr_layer", out var cl) && cl.TryGetInt32(out var currLayer))
                            CurrLayer = currLayer;
                        if (project.TryGetProperty("total_layers", out var tl) && tl.TryGetInt32(out var totalLayers))
                            TotalLayers = totalLayers;
                        // remain_time (and print_time) from the printer are in MINUTES, not seconds --
                        // confirmed against Slicer Next's own displayed remaining time (2h10m vs a raw
                        // value of 134, which only lines up as minutes: ~192min total - 54min elapsed =~138).
                        if (project.TryGetProperty("remain_time", out var rt) && rt.TryGetInt32(out var remainMinutes))
                            RemainTimeSeconds = remainMinutes * 60;
                        if (project.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                            Filename = fn.GetString();
                    }
                    break;

                case "tempature":
                    if (data.TryGetProperty("curr_nozzle_temp", out var nt) && nt.TryGetInt32(out var nozzleTemp))
                        NozzleTemp = nozzleTemp;
                    if (data.TryGetProperty("target_nozzle_temp", out var ntt) && ntt.TryGetInt32(out var nozzleTarget))
                        NozzleTargetTemp = nozzleTarget;
                    if (data.TryGetProperty("curr_hotbed_temp", out var bt) && bt.TryGetInt32(out var bedTemp))
                        BedTemp = bedTemp;
                    if (data.TryGetProperty("target_hotbed_temp", out var btt) && btt.TryGetInt32(out var bedTarget))
                        BedTargetTemp = bedTarget;
                    break;

                case "fan":
                    if (data.TryGetProperty("fan_speed_pct", out var fs) && fs.TryGetInt32(out var fanSpeed))
                        FanSpeedPct = fanSpeed;
                    if (data.TryGetProperty("print_speed_mode", out var psm) && psm.TryGetInt32(out var speedMode))
                        PrintSpeedMode = speedMode;
                    break;

                case "light":
                    if (data.TryGetProperty("lights", out var lights) && lights.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var light in lights.EnumerateArray())
                        {
                            if (light.TryGetProperty("type", out var lt) && lt.TryGetInt32(out var lightType))
                                LightType = lightType;
                            if (light.TryGetProperty("status", out var st) && st.TryGetInt32(out var lightStatus))
                                LightOn = lightStatus == 1;
                            if (light.TryGetProperty("brightness", out var br) && br.TryGetInt32(out var brightness))
                                LightBrightness = brightness;
                            break; // only one light on this printer so far
                        }
                    }
                    break;

                case "ota":
                    // Passive-only: this fires whenever gkapi's own reportVersion publish, or (if
                    // gkapi relays it locally) the real cloud server's reply, crosses this broker --
                    // never in response to anything Kobra LAN Monitor itself sends.
                    LatestOtaData = data.Clone();
                    LatestOtaAt = DateTimeOffset.UtcNow;
                    break;

                case "multiColorBox":
                    if (data.TryGetProperty("multi_color_box", out var boxes) && boxes.ValueKind == JsonValueKind.Array
                        && boxes.GetArrayLength() > 0)
                    {
                        var box = boxes[0];
                        if (box.TryGetProperty("temp", out var boxTemp) && boxTemp.TryGetInt32(out var bt2))
                            BoxTemp = bt2;
                        // Confirmed 13/09/2026 against a real multiColorBox/getInfo report: it carries
                        // id/status/model_id/auto_feed/loaded_slot/feed_status/temp/humidity/drying_status/
                        // slots and genuinely no version field at any level -- these three key names will
                        // never match on this printer. Left in defensively rather than removed in case a
                        // future firmware version adds one, but the real ACE Pro version can currently
                        // only come from the check-for-updates OTA exchange itself (AceProVersion stays
                        // null until that succeeds), not from passive status polling.
                        foreach (var key in (string[])["mcu_version", "firmware_version", "version"])
                        {
                            if (box.TryGetProperty(key, out var av) && av.ValueKind == JsonValueKind.String)
                            {
                                AceProVersion = av.GetString();
                                break;
                            }
                        }
                        if (box.TryGetProperty("loaded_slot", out var ls) && ls.TryGetInt32(out var loadedSlot))
                            LoadedSlot = loadedSlot;
                        if (box.TryGetProperty("drying_status", out var dryStatus) && dryStatus.ValueKind == JsonValueKind.Object)
                        {
                            if (dryStatus.TryGetProperty("status", out var ds) && ds.TryGetInt32(out var dryOn))
                                DryingOn = dryOn == 1;
                            if (dryStatus.TryGetProperty("target_temp", out var dt) && dt.TryGetInt32(out var dryTarget))
                                DryingTargetTemp = dryTarget;
                        }

                        if (box.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                        {
                            var parsedSlots = new List<FilamentSlot>();
                            foreach (var slot in slots.EnumerateArray())
                            {
                                var index = slot.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0;
                                var type = slot.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "" : "";
                                var colorHex = "#888888";
                                if (slot.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.Array
                                    && color.GetArrayLength() >= 3)
                                {
                                    var r = color[0].GetInt32();
                                    var g = color[1].GetInt32();
                                    var b = color[2].GetInt32();
                                    colorHex = $"#{r:X2}{g:X2}{b:X2}";
                                }
                                parsedSlots.Add(new FilamentSlot(index, type, colorHex));
                            }
                            FilamentSlots = parsedSlots;
                        }
                    }
                    break;
            }
        }
    }

    public PrinterStateSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new PrinterStateSnapshot(
                ConnectionStatus, Error, State, Progress, CurrLayer, TotalLayers,
                RemainTimeSeconds, Filename, NozzleTemp, NozzleTargetTemp, BedTemp, BedTargetTemp,
                FanSpeedPct, LightOn, LightBrightness, LightType, PrintSpeedMode, FilamentSlots, BoxTemp, LoadedSlot,
                DryingOn, DryingTargetTemp, PrinterVersion, AceProVersion,
                LatestOtaData, LatestOtaAt,
                LastUpdated, RawByTopic.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
    }
}

public record PrinterStateSnapshot(
    string ConnectionStatus,
    string? Error,
    string? State,
    int? Progress,
    int? CurrLayer,
    int? TotalLayers,
    int? RemainTimeSeconds,
    string? Filename,
    int? NozzleTemp,
    int? NozzleTargetTemp,
    int? BedTemp,
    int? BedTargetTemp,
    int? FanSpeedPct,
    bool? LightOn,
    int? LightBrightness,
    int? LightType,
    int? PrintSpeedMode,
    List<FilamentSlot>? FilamentSlots,
    int? BoxTemp,
    int? LoadedSlot,
    bool? DryingOn,
    int? DryingTargetTemp,
    string? PrinterVersion,
    string? AceProVersion,
    JsonElement? LatestOtaData,
    DateTimeOffset? LatestOtaAt,
    DateTimeOffset? LastUpdated,
    Dictionary<string, JsonElement> Raw);

public record FilamentSlot(int Index, string Type, string ColorHex);
