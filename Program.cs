using KobraLanMonitor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var httpPort = builder.Configuration.GetValue<int?>("HttpPort") ?? 8899;
builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");

var appSettings = AppSettings.Load();
builder.Services.AddSingleton(appSettings);
builder.Services.AddSingleton<PrinterState>();
builder.Services.AddSingleton<MqttMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttMonitorService>());

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "KobraLanMonitorAuth";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
    });

var app = builder.Build();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isPublicPath = path.StartsWithSegments("/setup") || path.StartsWithSegments("/login");

    if (!appSettings.IsConfigured)
    {
        if (!isPublicPath)
        {
            context.Response.Redirect("/setup");
            return;
        }
    }
    else if (!isPublicPath)
    {
        var authResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authResult.Succeeded)
        {
            context.Response.Redirect("/login");
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/setup", () =>
{
    if (appSettings.IsConfigured) return Results.Redirect("/login");
    return Results.Content(Pages.Setup(), "text/html");
});

app.MapPost("/setup", async (HttpContext ctx) =>
{
    if (appSettings.IsConfigured) return Results.Redirect("/login");

    var form = await ctx.Request.ReadFormAsync();
    var printerHost = form["printerHost"].ToString().Trim();
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();

    if (string.IsNullOrEmpty(printerHost) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
    {
        return Results.Content(Pages.Setup("All fields are required."), "text/html");
    }

    var id = Guid.NewGuid().ToString("N");
    appSettings.Printers.Add(new PrinterProfile(id, "Printer 1", printerHost));
    appSettings.ActivePrinterId = id;
    appSettings.AuthUsername = username;
    appSettings.AuthPasswordHash = PasswordHasher.Hash(password);
    appSettings.Save();

    await SignInAsync(ctx, username);
    return Results.Redirect("/");
});

app.MapGet("/login", () =>
{
    if (!appSettings.IsConfigured) return Results.Redirect("/setup");
    return Results.Content(Pages.Login(), "text/html");
});

app.MapPost("/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();

    if (username != appSettings.AuthUsername || appSettings.AuthPasswordHash == null
        || !PasswordHasher.Verify(password, appSettings.AuthPasswordHash))
    {
        return Results.Content(Pages.Login("Incorrect username or password."), "text/html");
    }

    await SignInAsync(ctx, username);
    return Results.Redirect("/");
});

app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// Multi-printer support (14/09/2026): most people have exactly one Anycubic printer, but plenty
// have more (an ACE-equipped machine alongside an older one, or just more than one on the same
// network). Only one printer is ever actively monitored at a time -- switching tears down the old
// MQTT connection and brings up a new one, rather than juggling several live sessions at once.
app.MapGet("/api/printers", () => Results.Json(
    appSettings.Printers.Select(p => new { p.Id, p.Name, p.Host, active = p.Id == appSettings.ActivePrinterId })));

app.MapPost("/api/printers", (AddPrinterRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Host))
        return Results.BadRequest(new { error = "name and host are required" });

    var profile = new PrinterProfile(Guid.NewGuid().ToString("N"), req.Name.Trim(), req.Host.Trim());
    appSettings.Printers.Add(profile);

    // The very first printer ever added (e.g. via /setup) is activated automatically; later
    // additions are just saved -- switching to one is a separate, deliberate action.
    if (appSettings.ActivePrinterId == null)
    {
        appSettings.ActivePrinterId = profile.Id;
    }
    appSettings.Save();
    return Results.Json(profile);
});

app.MapPut("/api/printers/{id}", (string id, AddPrinterRequest req, PrinterState state, MqttMonitorService mqtt) =>
{
    var index = appSettings.Printers.FindIndex(p => p.Id == id);
    if (index == -1) return Results.NotFound(new { error = "No such printer." });
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Host))
        return Results.BadRequest(new { error = "name and host are required" });

    var wasActive = appSettings.Printers[index].Id == appSettings.ActivePrinterId;
    var hostChanged = appSettings.Printers[index].Host != req.Host.Trim();
    appSettings.Printers[index] = new PrinterProfile(id, req.Name.Trim(), req.Host.Trim());
    appSettings.Save();

    if (wasActive && hostChanged)
    {
        state.Reset();
        mqtt.NotifyPrinterSwitched();
    }
    return Results.Json(appSettings.Printers[index]);
});

app.MapDelete("/api/printers/{id}", (string id, PrinterState state, MqttMonitorService mqtt) =>
{
    if (appSettings.Printers.Count <= 1)
        return Results.BadRequest(new { error = "Can't remove your only printer -- add another first." });

    var removed = appSettings.Printers.RemoveAll(p => p.Id == id) > 0;
    if (!removed) return Results.NotFound(new { error = "No such printer." });

    if (appSettings.ActivePrinterId == id)
    {
        appSettings.ActivePrinterId = appSettings.Printers[0].Id;
        state.Reset();
        mqtt.NotifyPrinterSwitched();
    }
    appSettings.Save();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/printers/{id}/activate", (string id, PrinterState state, MqttMonitorService mqtt) =>
{
    if (appSettings.Printers.All(p => p.Id != id)) return Results.NotFound(new { error = "No such printer." });
    if (appSettings.ActivePrinterId == id) return Results.Ok(new { ok = true }); // already active, nothing to do

    appSettings.ActivePrinterId = id;
    appSettings.Save();
    state.Reset();
    mqtt.NotifyPrinterSwitched();
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/status", (PrinterState state) => Results.Json(state.Snapshot()));

app.MapGet("/api/stream.mjpg", (HttpContext ctx, MqttMonitorService mqtt, ILoggerFactory lf) =>
    CameraStreamHandler.StreamAsync(ctx, appSettings, builder.Configuration, mqtt, lf.CreateLogger("CameraStream")));

app.MapPost("/api/camera/stop", async (MqttMonitorService mqtt, CancellationToken ct) =>
{
    var sent = await mqtt.SendVideoCaptureControlAsync(false, ct);
    return sent
        ? Results.Ok(new { ok = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/print/stop", async (MqttMonitorService mqtt, CancellationToken ct) =>
{
    var sent = await mqtt.SendPrintCommandAsync("stop", ct);
    return sent
        ? Results.Ok(new { ok = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/files", async (string target, string? path, MqttMonitorService mqtt, CancellationToken ct) =>
{
    var action = target switch
    {
        "local" => "listLocal",
        "usb" => "listUdisk",
        // Confirmed real wire command (13/09/2026, found as a literal "file:listVideo" string in
        // gkapi's own binary) -- same "file" type family as listLocal/listUdisk above, so treated
        // with the same confidence. Lists time-lapse video output specifically.
        "video" => "listVideo",
        _ => null,
    };
    if (action == null) return Results.BadRequest(new { error = "target must be 'local', 'usb', or 'video'" });

    var response = await mqtt.SendFileCommandAsync(action, new { path = string.IsNullOrEmpty(path) ? "/" : path }, ct);
    if (response == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    var files = FileListParser.Parse(response.Value);
    return Results.Json(files);
});

// NOT an active query -- confirmed 13/09/2026 (via Rinkhals' own working check_updates.py) that no
// such command exists on this firmware: gkapi auto-publishes reportVersion on every cloud connect,
// and the server's real answer (if any) arrives asynchronously on a plain "ota" topic that
// PrinterState.ApplyMessage already captures passively whenever it's overheard. So "checking" here
// just means "report whatever's already been seen" -- there is nothing this endpoint can do to force
// a fresh answer. Still requires the printer's own cloud connection to be up at all (LAN mode alone
// does not provide that), so an empty result is expected and correct, not a bug, until the real
// device.ini/cloud certs are in place (see the k3m-firmware-emulator project).
app.MapPost("/api/firmware/check", (PrinterState state) =>
{
    var result = new
    {
        printer = new { currentVersion = state.PrinterVersion },
        acePro = new { currentVersion = state.AceProVersion },
        latestOtaData = state.LatestOtaData,
        latestOtaAt = state.LatestOtaAt,
        checkedAt = DateTimeOffset.UtcNow,
    };

    // Saved locally so the popup can show "last checked" without re-deriving this every open --
    // "nothing overheard yet" is itself a real, informative result worth remembering.
    appSettings.LastFirmwareCheckJson = JsonSerializer.Serialize(result);
    appSettings.LastFirmwareCheckAt = result.checkedAt;
    appSettings.Save();

    return Results.Json(result);
});

// Whatever the last check-for-updates result was, without triggering a new (possibly slow,
// possibly cloud-dependent) query -- lets the popup show something useful the instant it opens.
app.MapGet("/api/firmware/last", () =>
{
    if (appSettings.LastFirmwareCheckJson == null) return Results.Json(new { checkedAt = (DateTimeOffset?)null });
    using var doc = JsonDocument.Parse(appSettings.LastFirmwareCheckJson);
    return Results.Text(doc.RootElement.GetRawText(), "application/json");
});

// Cloud-free update check (13/09/2026): compares the printer's real current version (already
// known locally via the LAN "info" report, see PrinterState.PrinterVersion) against the latest
// version listed in Data/firmware-manifest.json -- no Anycubic cloud account, device pairing, or
// authenticated download API involved anywhere in this path. This is genuinely what the whole
// cert/cloud investigation was trying to route around; this sidesteps it entirely.
//
// This is now OUR OWN maintained copy (Jason's explicit call, 13/09/2026), not a live third-party
// fetch -- see the manifest file's own "note" field for full provenance. It was seeded from
// jbatonnet/Rinkhals.Firmwares (a real, checksummed, actively-maintained community mirror --
// confirmed via their own build tooling, build/prepare-version.sh, which uses this exact source
// to fetch official firmware for their patch-building process). Worth being upfront about
// provenance regardless of who hosts the copy: this traces back to a community mirror, not
// Anycubic's own server -- Anycubic doesn't publish a public direct-download page for this model
// at all (checked wiki.anycubic.com directly; the firmware-upgrade-log page lists version history
// with no download links, and the firmware-software page doesn't list the Kobra 3 Max at all).
// Keeping this as our own file (rather than a live fetch) means adding other printer models later
// only requires editing our own JSON, not depending on Rinkhals tracking a model we care about.
app.MapGet("/api/firmware/public-latest", (string? model) =>
{
    var modelCode = string.IsNullOrWhiteSpace(model) ? "K3M" : model;
    try
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Data", "firmware-manifest.json");
        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.GetProperty("models").TryGetProperty(modelCode, out var modelEntry))
        {
            return Results.NotFound(new { error = $"No firmware entries tracked for model '{modelCode}'." });
        }

        JsonElement? latest = null;
        long latestDate = long.MinValue;
        foreach (var fw in modelEntry.GetProperty("firmwares").EnumerateArray())
        {
            var date = fw.TryGetProperty("date", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
            if (date > latestDate)
            {
                latestDate = date;
                latest = fw.Clone();
            }
        }

        if (latest == null) return Results.StatusCode(StatusCodes.Status502BadGateway);

        return Results.Json(new
        {
            version = latest.Value.GetProperty("version").GetString(),
            changes = latest.Value.TryGetProperty("changes", out var c) ? c.GetString() : null,
            url = latest.Value.GetProperty("url").GetString(),
            publishedAt = DateTimeOffset.FromUnixTimeSeconds(latestDate),
            modelStatus = modelEntry.GetProperty("status").GetString(),
            source = "Our own tracked copy (Data/firmware-manifest.json), seeded from jbatonnet/Rinkhals.Firmwares -- a community mirror, not Anycubic's own server.",
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/files/delete", async (DeleteFileRequest req, MqttMonitorService mqtt, PrinterState state, CancellationToken ct) =>
{
    var action = req.Target switch
    {
        "local" => "deleteLocal",
        "usb" => "deleteUdisk",
        _ => null,
    };
    if (action == null || string.IsNullOrWhiteSpace(req.FileName))
        return Results.BadRequest(new { error = "target must be 'local' or 'usb', filename is required" });

    if (req.Target == "local" && state.Filename == req.FileName)
        return Results.BadRequest(new { error = "Refusing to delete the file for the current print job." });

    var response = await mqtt.SendFileCommandAsync(action, new { path = req.Path ?? "/", filename = req.FileName }, ct);
    return response == null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(new { ok = true });
});

app.MapPost("/api/print/start", async (StartPrintRequest req, MqttMonitorService mqtt, PrinterState state, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.FileName))
        return Results.BadRequest(new { error = "filename is required" });

    if (state.State is "printing" or "paused")
        return Results.BadRequest(new { error = $"A print is already {state.State}. Stop it before starting another." });

    var sent = await mqtt.SendPrintStartAsync(req.FileName, req.Path ?? "/", ct);
    return sent
        ? Results.Ok(new { ok = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/light", async (LightRequest req, MqttMonitorService mqtt, PrinterState state, CancellationToken ct) =>
{
    var lightType = req.LightType ?? state.LightType ?? 1;
    var sent = await mqtt.SendLightControlAsync(lightType, req.On, req.Brightness ?? 100, ct);
    return sent
        ? Results.Ok(new { ok = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/drying", async (DryingRequest req, MqttMonitorService mqtt, CancellationToken ct) =>
{
    var sent = await mqtt.SendDryingControlAsync(req.On, req.TargetTemp ?? 50, req.DurationMinutes ?? 480, ct);
    return sent
        ? Results.Ok(new { ok = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

// Advanced/secondary features (13/09/2026) -- kept off the main dashboard deliberately (that page
// is for monitoring only), live on the separate advanced.html page instead. Each type/action pair
// below was found as a real, literal string in gkapi's own binary (not guessed) unless its own
// comment says otherwise -- see advanced.html for the per-feature confidence badge shown to the user.
app.MapPost("/api/advanced/disable-steppers", async (MqttMonitorService mqtt, CancellationToken ct) =>
{
    var response = await mqtt.SendGenericQueryAsync("axis", "turnOff", null, ct);
    return response == null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Json(new { ok = true, raw = response });
});

app.MapGet("/api/advanced/position", async (MqttMonitorService mqtt, CancellationToken ct) =>
{
    var response = await mqtt.SendGenericQueryAsync("axis", "query", null, ct);
    return response == null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Json(new { ok = true, raw = response });
});

app.MapPost("/api/advanced/feed-filament", async (FeedFilamentRequest req, MqttMonitorService mqtt, CancellationToken ct) =>
{
    // Field names (box_id, slot_index) confirmed real via gkapi's own struct tags; the overall
    // shape (flat under data, vs. nested like the confirmed multiColorBox:setDry command) is not
    // confirmed -- this is the best-effort guess, not a live-verified shape.
    var response = await mqtt.SendGenericQueryAsync("multiColorBox", "feedFilament",
        new { box_id = req.BoxId ?? 0, slot_index = req.SlotIndex }, ct);
    return response == null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Json(new { ok = true, raw = response });
});

// GUESS, not wire-confirmed (unlike feedFilament above): "unwindFilament" mirrors feedFilament's
// confirmed naming by symmetry only -- no literal "multiColorBox:unwindFilament" string was found
// in gkapi's own binary. A genuine separate UnwindFilament controller method does exist
// (printerApi/controller/filament_hub.FilamentHubController.UnwindFilament), but it's unconfirmed
// whether that's reachable this way at all, or only via the still-undeciphered port-80 API. Wired
// in anyway at Jason's request as a clearly-labeled experiment -- advanced.html marks this "guess"
// tier, distinct from feedFilament's "wire-confirmed" tier.
app.MapPost("/api/advanced/unwind-filament", async (FeedFilamentRequest req, MqttMonitorService mqtt, CancellationToken ct) =>
{
    var response = await mqtt.SendGenericQueryAsync("multiColorBox", "unwindFilament",
        new { box_id = req.BoxId ?? 0, slot_index = req.SlotIndex }, ct);
    return response == null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Json(new { ok = true, raw = response });
});

app.MapPost("/api/print/settings", async (PrintSettingsRequest req, MqttMonitorService mqtt, CancellationToken ct) =>
{
    var settings = new Dictionary<string, object>();
    if (req.TargetNozzleTemp is { } nozzle) settings["target_nozzle_temp"] = nozzle;
    if (req.TargetHotbedTemp is { } bed) settings["target_hotbed_temp"] = bed;
    if (req.FanSpeedPct is { } fan) settings["fan_speed_pct"] = fan;
    if (req.PrintSpeedMode is { } speed) settings["print_speed_mode"] = speed;
    if (settings.Count == 0) return Results.BadRequest(new { error = "at least one setting is required" });

    var sent = await mqtt.SendPrintUpdateAsync(settings, ct);
    return sent
        ? Results.Ok(new { ok = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/files/thumbnail", async (string target, string fileName, MqttMonitorService mqtt, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(fileName)) return Results.BadRequest(new { error = "fileName is required" });
    var root = target == "usb" ? "udisk" : "local";

    var response = await mqtt.SendFileCommandAsync("fileDetails", new { root, filename = fileName }, ct, useWebTopic: true);
    if (response == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    if (!response.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
        || !data.TryGetProperty("file_details", out var details) || details.ValueKind != JsonValueKind.Object)
    {
        return Results.NotFound(new { error = "No thumbnail in response." });
    }

    var thumbBase64 = details.TryGetProperty("png_image", out var png) && png.ValueKind == JsonValueKind.String
        ? png.GetString()
        : details.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.String ? thumb.GetString() : null;

    return thumbBase64 == null
        ? Results.NotFound(new { error = "No thumbnail in response." })
        : Results.Ok(new { thumbnailBase64 = thumbBase64 });
});

app.MapPost("/api/files/upload", async (HttpRequest req, MqttMonitorService mqtt, PrinterState state, ILoggerFactory lf, CancellationToken ct) =>
{
    var logger = lf.CreateLogger("FileUpload");
    if (string.IsNullOrEmpty(state.FileUploadUrl))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    if (!req.HasFormContentType) return Results.BadRequest(new { error = "multipart form upload required" });
    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Results.BadRequest(new { error = "no file provided" });

    // Undocumented endpoint (only known from the printer's own "info" report, urls.fileUploadurl) --
    // trying multipart/form-data first since that's the most common shape for a plain HTTP upload
    // sink; not yet live-verified.
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    using var content = new MultipartFormDataContent();
    await using var fileStream = file.OpenReadStream();
    using var streamContent = new StreamContent(fileStream);
    content.Add(streamContent, "file", file.FileName);

    try
    {
        using var response = await httpClient.PostAsync(state.FileUploadUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("Upload response ({Status}): {Body}", response.StatusCode, body);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { ok = true, printerResponse = body })
            : Results.StatusCode((int)response.StatusCode);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Upload to printer failed");
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
});

app.Run();

static async Task SignInAsync(HttpContext ctx, string username)
{
    var claims = new[] { new Claim(ClaimTypes.Name, username) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
}

record AddPrinterRequest(string Name, string Host);
record DeleteFileRequest(string Target, string? Path, string FileName);
record StartPrintRequest(string FileName, string? Path);
record LightRequest(bool On, int? Brightness, int? LightType);
record DryingRequest(bool On, int? TargetTemp, int? DurationMinutes);
record FeedFilamentRequest(int? BoxId, int SlotIndex);
record PrintSettingsRequest(int? TargetNozzleTemp, int? TargetHotbedTemp, int? FanSpeedPct, int? PrintSpeedMode);
