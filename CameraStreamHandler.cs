using System.Diagnostics;
using System.Text;

namespace KobraLanMonitor;

/// <summary>
/// Proxies the printer's camera as a genuine live MJPEG stream (multipart/x-mixed-replace), which
/// browsers can render directly in an &lt;img&gt; tag with no client-side polling. Spawns one ffmpeg
/// process per viewer, tied to that request's lifetime -- killed automatically when the browser
/// disconnects (tab closed/navigated away), via the request's CancellationToken.
/// </summary>
public static class CameraStreamHandler
{
    private const string Boundary = "kobraframe";

    public static async Task StreamAsync(HttpContext ctx, AppSettings appSettings, IConfiguration config, MqttMonitorService mqtt, ILogger logger)
    {
        var printer = appSettings.ActivePrinter;
        if (printer == null || string.IsNullOrEmpty(printer.Host))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var ffmpegPath = config["FfmpegPath"] ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        var ct = ctx.RequestAborted;
        var useNetworkCamera = printer.EffectiveCameraSource == "network" && printer.HasNetworkCamera;

        string streamUrl;
        if (useNetworkCamera)
        {
            // A separate camera on its own network connection -- doesn't touch the printer's own
            // onboard video pipeline at all, and isn't affected by (or competing for a slot on)
            // whatever else has the printer's own camera panel open elsewhere. See the K3M Journey,
            // Day 16, for why that independence turned out to matter in practice, not just in theory.
            streamUrl = printer.BuildNetworkCameraRtspUrl();
        }
        else
        {
            streamUrl = $"http://{printer.Host}:18088/flv";

            // The printer's :18088/flv endpoint serves no frames at all until told to start its
            // video encoder -- captured live from Slicer Next's own camera Play button. Safe to
            // send even if another viewer already has it running (idempotent on the printer's side).
            // Not applicable to a network camera, which is always just streaming on its own.
            await mqtt.SendVideoCaptureControlAsync(true, ct);
            await Task.Delay(400, ct);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (useNetworkCamera)
        {
            // Without this, ffmpeg defaults to UDP transport for RTSP -- which a lot of
            // consumer cameras (EZVIZ included) don't serve reliably, so the connection just
            // sits open with no frames arriving rather than failing outright. VLC auto-negotiates
            // around this silently; a bare ffmpeg -i rtsp://... call doesn't. Confirmed live
            // 15/09/2026: the exact same URL connected fine in VLC while this hung on
            // "Connecting..." indefinitely without this flag. Matches Kobra Time Lapse's own
            // already-working RTSP capture, which has always used this flag.
            psi.ArgumentList.Add("-rtsp_transport");
            psi.ArgumentList.Add("tcp");
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(streamUrl);

        // Rotation applied here, server-side, to the actual frames -- not a CSS transform on
        // the client. Simpler on the browser side (no aspect-ratio/box-size juggling for a
        // portrait-shaped result -- object-fit:contain already handles a real portrait frame
        // correctly) and means the rotation is genuinely "baked in" the same way for every
        // viewer, not a per-browser display quirk. ffmpeg's transpose values are individually
        // 90 degrees, not degrees-as-a-number, hence the explicit map rather than passing
        // rotationDeg straight through: 1=90 CW, 2=90 CCW, and 180 is just two 90s stacked
        // (transpose has no native 180 mode of its own).
        var transposeFilter = printer.CameraRotationDeg switch
        {
            90 => "transpose=1",
            180 => "transpose=1,transpose=1",
            270 => "transpose=2",
            _ => null
        };
        if (transposeFilter != null)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(transposeFilter);
        }

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("mjpeg");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("5");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("10");
        psi.ArgumentList.Add("pipe:1");

        using var process = Process.Start(psi);
        if (process == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });

        // stderr is redirected above but was never actually read here -- the exact same
        // deadlock already found and fixed in Kobra Time Lapse's own ffmpeg handling
        // (CaptureService.cs, 15/09/2026): ffmpeg writes continuous diagnostic/timing output
        // to stderr while it runs, and if nothing drains that pipe, the OS buffer fills and
        // ffmpeg itself blocks trying to write to it -- not a network or auth failure, a
        // genuine deadlock. RTSP streams log far more transport/negotiation chatter than the
        // onboard camera's plain HTTP pull, which is likely why this path hung specifically.
        // Drained continuously and thrown away (nothing here currently parses it), just to
        // keep the pipe from ever backing up.
        _ = Task.Run(async () =>
        {
            try
            {
                var buf = new char[4096];
                while (await process.StandardError.ReadAsync(buf, ct) > 0) { }
            }
            catch { /* process exited/killed -- expected */ }
        }, ct);

        ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={Boundary}";
        ctx.Response.Headers.CacheControl = "no-cache";

        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var buffer = new List<byte>(64 * 1024);
            var readBuf = new byte[16 * 1024];

            while (!ct.IsCancellationRequested)
            {
                var n = await stdout.ReadAsync(readBuf, ct);
                if (n <= 0) break;

                buffer.AddRange(new ArraySegment<byte>(readBuf, 0, n));

                int frameEnd;
                while ((frameEnd = FindJpegEnd(buffer)) >= 0)
                {
                    var frame = buffer.GetRange(0, frameEnd + 2).ToArray();
                    buffer.RemoveRange(0, frameEnd + 2);

                    if (frame.Length < 4) continue; // not a real frame, skip

                    var header = $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n";
                    await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
                    await ctx.Response.Body.WriteAsync(frame, ct);
                    await ctx.Response.Body.WriteAsync("\r\n"u8.ToArray(), ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Viewer disconnected -- expected.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Camera stream ended");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            }
        }
    }

    /// <summary>Finds the JPEG End-Of-Image marker (0xFFD9) so we know a full frame is buffered.</summary>
    private static int FindJpegEnd(List<byte> buffer)
    {
        for (var i = 0; i < buffer.Count - 1; i++)
        {
            if (buffer[i] == 0xFF && buffer[i + 1] == 0xD9)
                return i;
        }
        return -1;
    }
}
