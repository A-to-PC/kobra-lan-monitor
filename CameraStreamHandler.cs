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

    public static async Task StreamAsync(HttpContext ctx, AppSettings appSettings, IConfiguration config, ILogger logger)
    {
        if (string.IsNullOrEmpty(appSettings.PrinterHost))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var ffmpegPath = config["FfmpegPath"] ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        var streamUrl = $"http://{appSettings.PrinterHost}:18088/flv";
        var ct = ctx.RequestAborted;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            ArgumentList =
            {
                "-i", streamUrl,
                "-f", "mjpeg",
                "-q:v", "5",
                "-r", "10",
                "pipe:1",
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

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
