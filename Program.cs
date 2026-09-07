using KobraLanMonitor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var httpPort = builder.Configuration.GetValue<int?>("HttpPort") ?? 8080;
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

    appSettings.PrinterHost = printerHost;
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

app.MapGet("/api/status", (PrinterState state) => Results.Json(state.Snapshot()));

app.MapGet("/api/stream.mjpg", (HttpContext ctx, ILoggerFactory lf) =>
    CameraStreamHandler.StreamAsync(ctx, appSettings, builder.Configuration, lf.CreateLogger("CameraStream")));

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
        _ => null,
    };
    if (action == null) return Results.BadRequest(new { error = "target must be 'local' or 'usb'" });

    var response = await mqtt.SendFileCommandAsync(action, new { path = string.IsNullOrEmpty(path) ? "/" : path }, ct);
    if (response == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    var files = FileListParser.Parse(response.Value);
    return Results.Json(files);
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

record DeleteFileRequest(string Target, string? Path, string FileName);
record StartPrintRequest(string FileName, string? Path);
record LightRequest(bool On, int? Brightness, int? LightType);
record DryingRequest(bool On, int? TargetTemp, int? DurationMinutes);
record PrintSettingsRequest(int? TargetNozzleTemp, int? TargetHotbedTemp, int? FanSpeedPct, int? PrintSpeedMode);
