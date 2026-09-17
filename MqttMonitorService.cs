using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;

namespace KobraLanMonitor;

public class MqttMonitorService(PrinterState state, AppSettings appSettings, IConfiguration config, ILogger<MqttMonitorService> logger)
    : BackgroundService
{
    private static readonly string[] QueryTypes = ["status", "info", "tempature", "fan", "light", "peripherie", "multiColorBox"];

    private readonly int _queryIntervalSeconds = config.GetValue<int?>("QueryIntervalSeconds") ?? 2;

    // Set only while a live, connected MQTT session exists, so the web layer can send real
    // print-control commands (e.g. the emergency stop button) through the same connection.
    private volatile IMqttClient? _currentClient;
    private volatile string? _queryTopicBase;
    private volatile string? _printCommandTopic;
    private volatile string? _fileCommandTopic;
    private volatile string? _webFileCommandTopic;
    private volatile string? _lightCommandTopic;
    private volatile string? _multiColorBoxCommandTopic;
    private volatile string? _videoCommandTopic;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingFileRequests = new();

    // Real-world capture (14/09/2026) showed the firmware answers an "axis" query with a genuine
    // "axis/report" broadcast -- but unlike file/video queries, it doesn't echo the requester's own
    // msgid back, so the strict msgid match in _pendingFileRequests never resolves it and the caller
    // times out even though the printer genuinely responded. This is a fallback keyed by query type
    // instead of msgid: the next report of that type resolves it, whichever request is waiting longest.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingByType = new();

    // Tracks real traffic from the printer, independent of MQTTnet's own IsConnected flag -- a TCP
    // connection can go half-dead (printer stops responding, socket never notices) while IsConnected
    // stays true for hours, since without keepalive there's nothing actively probing it. If no message
    // arrives for too long, that's treated as a dead connection and forces a reconnect.
    private volatile int _lastMessageTicks = Environment.TickCount;
    private static readonly TimeSpan StaleConnectionTimeout = TimeSpan.FromSeconds(30);

    // Set only while ExecuteAsync is inside a RunOnceAsync call, so a printer switch can cancel
    // just that one connection attempt without tearing down the whole BackgroundService the way
    // cancelling stoppingToken itself would.
    private volatile CancellationTokenSource? _switchRequested;

    /// <summary>
    /// Cancels whatever connection is currently active (or connecting) so the monitor loop drops
    /// it and immediately reconnects to whichever printer is now active in AppSettings, instead of
    /// waiting for the current one to fail or for the normal 10s retry delay.
    /// </summary>
    public void NotifyPrinterSwitched() => _switchRequested?.Cancel();

    /// <summary>
    /// Sends a print-control command. Pause/resume are confirmed via live capture of Slicer Next's own
    /// MQTT traffic; "stop" is confirmed against the documented Kobra 3 MQTT command reference
    /// (rvanderp3/kobra-connect docs/mqtt-commands.md) -- not a guess (an earlier "end" inference was wrong).
    /// Returns false if there's no live connection to send it through -- callers must treat that as a
    /// real failure, not a silent no-op, especially for the emergency stop button.
    /// </summary>
    public async Task<bool> SendPrintCommandAsync(string action, CancellationToken ct)
    {
        var client = _currentClient;
        var topic = _printCommandTopic;
        if (client is not { IsConnected: true } || topic == null)
        {
            logger.LogWarning("Cannot send print command '{Action}' -- no live MQTT connection", action);
            return false;
        }

        var payload = new
        {
            type = "print",
            action,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = new { taskid = "-1" },
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        logger.LogInformation("Sending print command: {Action}", action);
        await client.PublishAsync(message, ct);
        return true;
    }

    /// <summary>
    /// Starts printing a file already sitting on the printer's own storage (list-then-print, not an
    /// upload). Topic/payload shape per the documented Kobra 3 MQTT command reference
    /// (rvanderp3/kobra-connect docs/mqtt-commands.md) -- confirmed live: Jason has fired off multiple
    /// real prints from the Monitor UI using this exact command.
    /// </summary>
    public async Task<bool> SendPrintStartAsync(string fileName, string path, CancellationToken ct)
    {
        var client = _currentClient;
        var topic = _printCommandTopic;
        if (client is not { IsConnected: true } || topic == null)
        {
            logger.LogWarning("Cannot start print '{FileName}' -- no live MQTT connection", fileName);
            return false;
        }

        var payload = new
        {
            type = "print",
            action = "start",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = new
            {
                taskid = "-1",
                filename = fileName,
                filepath = path,
                filetype = 1,
            },
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        logger.LogInformation("Starting print: {FileName}", fileName);
        await client.PublishAsync(message, ct);
        return true;
    }

    /// <summary>
    /// Turns a light on/off with a brightness level. Topic/payload confirmed against the documented
    /// Kobra 3 MQTT command reference (rvanderp3/kobra-connect) -- not yet live-verified (no light
    /// toggle has been fired against the real printer). type: 3 = camera light, 1 = head light,
    /// 2 = chamber light (KS1/S1 only). Pass state.LightType when known so it matches whichever
    /// light this printer actually reports.
    /// </summary>
    public async Task<bool> SendLightControlAsync(int lightType, bool on, int brightness, CancellationToken ct)
    {
        var client = _currentClient;
        var topic = _lightCommandTopic;
        if (client is not { IsConnected: true } || topic == null)
        {
            logger.LogWarning("Cannot control light -- no live MQTT connection");
            return false;
        }

        var payload = new
        {
            type = "light",
            action = "control",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = new { type = lightType, status = on ? 1 : 0, brightness = on ? brightness : 0 },
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        logger.LogInformation("Setting light type {LightType}: on={On} brightness={Brightness}", lightType, on, brightness);
        await client.PublishAsync(message, ct);
        return true;
    }

    /// <summary>
    /// Tells the printer to start/stop its video encoder. Captured live from Slicer Next's own camera
    /// Play/Stop button -- the printer's :18088/flv stream serves no frames at all until this is sent,
    /// which is why our own camera stream previously appeared to depend on Slicer Next being open.
    /// Topic: .../web/printer/{model}/{device}/video, action "startCapture"/"stopCapture".
    /// </summary>
    public async Task<bool> SendVideoCaptureControlAsync(bool start, CancellationToken ct)
    {
        var client = _currentClient;
        var topic = _videoCommandTopic;
        if (client is not { IsConnected: true } || topic == null)
        {
            logger.LogWarning("Cannot control video capture -- no live MQTT connection");
            return false;
        }

        var payload = new
        {
            type = "video",
            action = start ? "startCapture" : "stopCapture",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = (object?)null,
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        logger.LogInformation("Video capture: {Action}", start ? "startCapture" : "stopCapture");
        await client.PublishAsync(message, ct);
        return true;
    }

    /// <summary>
    /// Turns the ACE box's filament dryer on/off. Topic/payload captured live from Slicer Next's own
    /// traffic (WEB-CMD capture) when Jason toggled Drying on in Material Management -- genuinely
    /// confirmed, not an inference: {"type":"multiColorBox","action":"setDry","data":{"multi_color_box":
    /// [{"id":0,"drying_status":{"status":1,"target_temp":50,"duration":480}}]}}.
    /// </summary>
    public async Task<bool> SendDryingControlAsync(bool on, int targetTemp, int durationMinutes, CancellationToken ct)
    {
        var client = _currentClient;
        var topic = _multiColorBoxCommandTopic;
        if (client is not { IsConnected: true } || topic == null)
        {
            logger.LogWarning("Cannot control drying -- no live MQTT connection");
            return false;
        }

        var payload = new
        {
            type = "multiColorBox",
            action = "setDry",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = new
            {
                multi_color_box = new[]
                {
                    new { id = 0, drying_status = new { status = on ? 1 : 0, target_temp = targetTemp, duration = durationMinutes } },
                },
            },
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        logger.LogInformation("Setting drying: on={On} targetTemp={TargetTemp} duration={Duration}", on, targetTemp, durationMinutes);
        await client.PublishAsync(message, ct);
        return true;
    }

    /// <summary>
    /// Updates live print settings (nozzle/bed target temp, fan speed, print speed mode, z
    /// compensation) -- only the fields present in <paramref name="settings"/> are changed on the
    /// printer's side. Topic/payload per the documented Kobra 3 MQTT command reference
    /// (rvanderp3/kobra-connect) -- not yet live-verified.
    /// </summary>
    public async Task<bool> SendPrintUpdateAsync(object settings, CancellationToken ct)
    {
        var client = _currentClient;
        var topic = _printCommandTopic;
        if (client is not { IsConnected: true } || topic == null)
        {
            logger.LogWarning("Cannot update print settings -- no live MQTT connection");
            return false;
        }

        var payload = new
        {
            type = "print",
            action = "update",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = new { taskid = "-1", settings },
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        logger.LogInformation("Sending print settings update: {Settings}", JsonSerializer.Serialize(settings));
        await client.PublishAsync(message, ct);
        return true;
    }

    /// <summary>
    /// Sends a storage file command (list/delete on local cache or USB, or fileDetails for a thumbnail)
    /// and awaits the matching response by msgid. list/delete confirmed live against the real printer;
    /// fileDetails topic/shape confirmed by capturing Slicer Next's own live traffic (WEB-CMD capture).
    /// Set <paramref name="useWebTopic"/> for fileDetails, which Slicer Next sends via the "web" sender
    /// topic rather than the "slicer" sender topic list/delete use. Returns null on no connection or a
    /// timed-out response.
    /// </summary>
    public async Task<JsonElement?> SendFileCommandAsync(string action, object? data, CancellationToken ct, bool useWebTopic = false)
    {
        var client = _currentClient;
        var topic = useWebTopic ? _webFileCommandTopic : _fileCommandTopic;
        if (client is not { IsConnected: true } || topic == null)
        {
            logger.LogWarning("Cannot send file command '{Action}' -- no live MQTT connection", action);
            return null;
        }

        var msgid = Guid.NewGuid().ToString();
        var payload = new
        {
            type = "file",
            action,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid,
            data,
        };

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFileRequests[msgid] = tcs;

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(JsonSerializer.Serialize(payload))
                .Build();

            logger.LogInformation("Sending file command: {Action} (msgid {Msgid})", action, msgid);
            await client.PublishAsync(message, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("File command '{Action}' timed out waiting for a response", action);
                return null;
            }
        }
        finally
        {
            _pendingFileRequests.TryRemove(msgid, out _);
        }
    }

    /// <summary>
    /// Sends a query on an arbitrary type/action pair and awaits the matching response by msgid, the
    /// same pending-request plumbing <see cref="SendFileCommandAsync"/> uses. General-purpose --
    /// backs the Advanced page's disable-steppers/position/feed-filament/unwind-filament/export-video
    /// features, each a real command found as a literal string in gkapi's own binary (not guessed),
    /// with per-feature confidence (wire-confirmed/live-verified/guess) tracked in the UI itself
    /// rather than here. Reusable for any other real type/action pair found later.
    /// </summary>
    public async Task<JsonElement?> SendGenericQueryAsync(string type, string action, object? data, CancellationToken ct)
    {
        var client = _currentClient;
        var topicBase = _queryTopicBase;
        if (client is not { IsConnected: true } || topicBase == null) return null;

        var msgid = Guid.NewGuid().ToString();
        var payload = new { type, action, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), msgid, data };
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFileRequests[msgid] = tcs;
        // Only registered as the type-fallback if nothing else is already waiting on this type --
        // deliberately not a queue, so a flood of the same query type can't pile up stale waiters.
        _pendingByType.TryAdd(type, tcs);

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"{topicBase}/{type}")
                .WithPayload(JsonSerializer.Serialize(payload))
                .Build();
            await client.PublishAsync(message, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            _pendingFileRequests.TryRemove(msgid, out _);
            // Only remove it if it's still ours -- a later SendGenericQueryAsync call for the same
            // type may have already registered its own tcs in the meantime.
            _pendingByType.TryRemove(new KeyValuePair<string, TaskCompletionSource<JsonElement>>(type, tcs));
        }
    }

    /// <summary>
    /// Fires a type/action command without waiting for any reply. Real-world testing (14/09/2026)
    /// showed steppers-off and ACE Pro feed/unwind commands genuinely execute against the printer --
    /// browsing videos and the version check kept working through the same MQTT session -- but never
    /// produce a msgid-matched response the way file/video queries reliably do, so waiting for one via
    /// SendGenericQueryAsync always timed out and reported a false "no connection" failure. Same
    /// fire-and-forget shape as SendLightControlAsync/SendPrintCommandAsync, which were never affected.
    /// </summary>
    public async Task<bool> SendGenericCommandAsync(string type, string action, object? data, CancellationToken ct)
    {
        var client = _currentClient;
        var topicBase = _queryTopicBase;
        if (client is not { IsConnected: true } || topicBase == null)
        {
            logger.LogWarning("Cannot send {Type}:{Action} -- no live MQTT connection", type, action);
            return false;
        }

        var payload = new
        {
            type,
            action,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data,
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"{topicBase}/{type}")
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        logger.LogInformation("Sending {Type}:{Action}", type, action);
        await client.PublishAsync(message, ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var activeHost = appSettings.ActivePrinter?.Host;
            if (string.IsNullOrEmpty(activeHost))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var switchCts = new CancellationTokenSource();
            _switchRequested = switchCts;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, switchCts.Token);
            var wasSwitch = false;

            try
            {
                await RunOnceAsync(activeHost, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested) break;
                wasSwitch = switchCts.IsCancellationRequested;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT monitor loop failed, retrying in 10s");
                state.ConnectionStatus = "error";
                state.Error = ex.Message;
            }
            finally
            {
                _switchRequested = null;
                switchCts.Dispose();
                _currentClient = null;
                _printCommandTopic = null;
                _fileCommandTopic = null;
                _webFileCommandTopic = null;
                _lightCommandTopic = null;
                _multiColorBoxCommandTopic = null;
                _videoCommandTopic = null;
                foreach (var key in _pendingFileRequests.Keys.ToArray())
                {
                    if (_pendingFileRequests.TryRemove(key, out var pending))
                        pending.TrySetCanceled();
                }
                _pendingByType.Clear();
            }

            if (stoppingToken.IsCancellationRequested) break;
            // A printer switch means a different host is already waiting to be connected to --
            // reconnect immediately instead of sitting through the normal 10s retry delay.
            if (wasSwitch) continue;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(string printerHost, CancellationToken ct)
    {
        logger.LogInformation("Discovering LAN credentials for {Host}", printerHost);
        state.ConnectionStatus = "connecting";
        var creds = await LanCredentialDiscovery.DiscoverAsync(printerHost, ct);

        var brokerUri = new Uri(creds.Broker.Replace("mqtts://", "https://"));
        var clientCert = X509Certificate2.CreateFromPem(creds.DeviceCrt, creds.DevicePk);

        var mqttFactory = new MqttClientFactory();
        using var client = mqttFactory.CreateMqttClient();

        // The printer reports on .../printer/<category>/<modelId>/<deviceId>/... (category
        // is usually "public"), while queries are published to .../web/printer/<modelId>/<deviceId>/<type>.
        // Confirmed against ha-anycubic-kobra-x-lan's coordinator.py.
        var reportSubscription = $"anycubic/anycubicCloud/v1/printer/+/{creds.ModelId}/{creds.DeviceId}/#";
        var queryTopicBase = $"anycubic/anycubicCloud/v1/web/printer/{creds.ModelId}/{creds.DeviceId}";
        // File commands (list/delete) must go out on the "slicer" sender topic, per the documented
        // Kobra 3 MQTT command reference (rvanderp3/kobra-connect) and confirmed independently by
        // SlimQuiggle/KobraCache, an open-source tool with this working live.
        var slicerTopicBase = $"anycubic/anycubicCloud/v1/slicer/printer/{creds.ModelId}/{creds.DeviceId}";

        client.ApplicationMessageReceivedAsync += e =>
        {
            _lastMessageTicks = Environment.TickCount;

            var topic = e.ApplicationMessage.Topic;
            var payloadText = e.ApplicationMessage.ConvertPayloadToString();

            try
            {
                if (string.IsNullOrEmpty(payloadText)) return Task.CompletedTask;

                using var doc = JsonDocument.Parse(payloadText);

                // Some messages on this msgid are bare acks like {"msgid":"..."} with no "type" -- those
                // aren't the real response, so don't consume the pending request for them; wait for the
                // one that actually carries a "type" field.
                var consumedByMsgid = false;
                if (doc.RootElement.TryGetProperty("msgid", out var msgidEl) && msgidEl.ValueKind == JsonValueKind.String
                    && msgidEl.GetString() is { } msgid && doc.RootElement.TryGetProperty("type", out _)
                    && _pendingFileRequests.TryRemove(msgid, out var pending))
                {
                    pending.TrySetResult(doc.RootElement.Clone());
                    consumedByMsgid = true;
                }

                var reportType = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()!
                    : topic.Split('/')[^1];

                // Fallback for types (confirmed for "axis" 14/09/2026) whose reply never carries the
                // requester's own msgid -- see _pendingByType's own comment for the full story.
                if (!consumedByMsgid && _pendingByType.TryRemove(reportType, out var typePending))
                {
                    typePending.TrySetResult(doc.RootElement.Clone());
                }

                logger.LogInformation("MQTT <- {Topic} ({Type})", topic, reportType);
                state.ApplyMessage(reportType, doc.RootElement);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse MQTT message on topic {Topic}: {Payload}", topic, payloadText);
            }

            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithClientId($"KobraLanMonitor-{Guid.NewGuid():N}")
            .WithTcpServer(brokerUri.Host, brokerUri.Port)
            .WithCredentials(creds.Username, creds.Password)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithTlsOptions(o =>
            {
                o.UseTls();
                o.WithClientCertificates(new List<X509Certificate2> { clientCert });
                o.WithCertificateValidationHandler(_ => true);
            })
            .WithCleanSession()
            .Build();

        logger.LogInformation("Connecting to MQTT broker {Broker}", creds.Broker);
        await client.ConnectAsync(options, ct);
        state.ConnectionStatus = "connected";
        state.Error = null;

        var subResult = await client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(reportSubscription)
                .Build(), ct);
        foreach (var item in subResult.Items)
        {
            logger.LogInformation("Subscribed to {Topic}: {Result}", item.TopicFilter.Topic, item.ResultCode);
        }

        _currentClient = client;
        _queryTopicBase = queryTopicBase;
        _printCommandTopic = $"{queryTopicBase}/print";
        _fileCommandTopic = $"{slicerTopicBase}/file";
        _webFileCommandTopic = $"{queryTopicBase}/file";
        _lightCommandTopic = $"{queryTopicBase}/light";
        _multiColorBoxCommandTopic = $"{queryTopicBase}/multiColorBox";
        _videoCommandTopic = $"{queryTopicBase}/video";
        _lastMessageTicks = Environment.TickCount;

        while (!ct.IsCancellationRequested && client.IsConnected)
        {
            if (IsConnectionStale())
            {
                logger.LogWarning(
                    "No MQTT traffic received in over {Seconds}s despite IsConnected still reporting true -- " +
                    "treating the connection as dead and forcing a reconnect", StaleConnectionTimeout.TotalSeconds);
                break;
            }

            foreach (var queryType in QueryTypes)
            {
                await PublishQueryAsync(client, queryTopicBase, queryType, ct);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_queryIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        state.ConnectionStatus = "disconnected";
    }

    /// <summary>
    /// True once too long has passed since any MQTT message was actually received, regardless of what
    /// MQTTnet's own IsConnected flag reports. Uses Environment.TickCount so this stays correct across
    /// its ~25-day wraparound (plain subtraction is the standard-safe idiom for that).
    /// </summary>
    private bool IsConnectionStale() =>
        unchecked(Environment.TickCount - _lastMessageTicks) > StaleConnectionTimeout.TotalMilliseconds;

    private async Task PublishQueryAsync(IMqttClient client, string queryTopicBase, string queryType, CancellationToken ct)
    {
        var payload = new
        {
            type = queryType,
            action = queryType == "multiColorBox" ? "getInfo" : "query",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            msgid = Guid.NewGuid().ToString(),
            data = (object?)null,
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"{queryTopicBase}/{queryType}")
            .WithPayload(JsonSerializer.Serialize(payload))
            .Build();

        await client.PublishAsync(message, ct);
    }
}
