# Kobra LAN Monitor

A self-hosted web dashboard for the Anycubic Kobra 3 (and likely other Kobra 3-generation printers), built by fully reverse-engineering Anycubic's local LAN protocol — no cloud account, no Anycubic app, no third-party relay. It talks directly to your printer over your own network.

Live status, camera streaming, print control, ACE (multi-material) filament and drying control, and remote file/print management, all from a browser on any device on your LAN.

## Features

**Every feature is now live-verified against a real printer — nothing left untested:**
- Live status: state, progress, layer, ETA, nozzle/bed temps, fan, filament slots
- Genuine continuous camera stream (MJPEG, low latency — no polling), with its own Start/Stop control — fully independent of Slicer Next
- Pause / Resume / Emergency Stop
- File browser (local storage + USB): list, folder navigation, delete, preview thumbnails, upload from your PC, and start a print from any file
- ACE box: filament colours/types, active-slot indicator, drying on/off
- Controls card — light on/off, nozzle/bed temperature, fan speed, print-speed-mode adjustment mid-print (each confirmed live: a commanded value shows up as the real target temp/speed on the printer itself, not just in the UI). Light brightness isn't included — confirmed via Slicer Next's own UI that this light hardware isn't dimmable, on/off is all it supports.

If a date shows up blank next to a file, that's deliberate — the printer sometimes reports a bogus tiny counter instead of a real timestamp for files tied to a print task, and the UI hides it rather than show something wrong.

**A standalone Windows installer is coming soon** — a self-contained one-click `.exe` (bundled ffmpeg, no .NET SDK required) so the manual setup steps below become optional. Watch this repo for the release.

## Why this exists

Anycubic's own apps (Slicer Next, the Anycubic Cloud app) require a cloud account and don't expose everything the printer's own LAN protocol actually supports. This project talks to the printer the same way those official apps do — a local HTTP handshake to discover per-session MQTT credentials, then mutual-TLS MQTT directly to the printer — just without the cloud account requirement.

Credentials are never hardcoded. Every session performs its own live handshake against your printer's IP to obtain fresh, printer-specific credentials. Nothing here embeds Anycubic's shared fleet secrets.

## Requirements

- .NET 10 SDK
- `ffmpeg.exe` (Windows build) placed in the project root — not bundled in this repo (keeps the repo small; this is a third-party binary). Grab a static Windows build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) and drop `ffmpeg.exe` next to `KobraLanMonitor.csproj`.
- Your printer must be on the same LAN, with LAN mode reachable (this is the same connection Slicer Next itself uses).
- A camera — a generic USB webcam works fine on stock firmware, no hacking required, despite what Anycubic's documentation implies about needing their own module.
  - Confirmed working cleanly: **Microsoft LifeCam HD-3000** (720p).
  - Confirmed detected and streaming, but with a "doubled frame" artifact (part of the image is a stale previous frame): **Razer Kiyo** (defaults to 1080p). Reproduced identically in Anycubic's own Slicer Next, so it's the printer's own onboard video pipeline, not something this app or its client can fix.
  - Working theory (two data points, not proven): Anycubic's own official camera accessory is 720p, matching the HD-3000 that works cleanly — the printer's video pipeline may simply be tuned for that resolution and mishandle higher ones like the Kiyo's default 1080p. **A 720p USB webcam is the safer bet** until this gets more data points.

## Setup

**1. Get the code onto a permanent location** — this isn't something to run from a Downloads or temp folder, since it'll keep running long-term and stores its settings next to itself.

- With git: `git clone https://github.com/A-to-PC/kobra-lan-monitor.git C:\Apps\KobraLanMonitor`
- Without git: on this page, click **Code → Download ZIP**, then extract it to a permanent folder, e.g. `C:\Apps\KobraLanMonitor`

**2. Add ffmpeg.** Download a static Windows build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) (the "release essentials" zip is fine), and copy `ffmpeg.exe` out of its `bin` folder into the project folder from step 1 — the same folder that has `KobraLanMonitor.csproj` in it.

**3. Build a runnable copy.** From that same folder:

```
dotnet publish -c Release -o publish
```

This creates a `publish` subfolder — that's the actual app. You can ignore the source files after this; `publish` is what you'll run and leave in place.

**4. Run it:**

```
cd publish
dotnet KobraLanMonitor.dll --HttpPort=8090
```

Leave that window open (or run it via Task Scheduler / a Windows service if you want it to survive reboots — not covered here yet).

**5. Open `http://localhost:8090`** (or `http://<your-pc-ip>:8090` from another device on the same network), and complete the one-time setup form: your printer's LAN IP address, and a username/password for the dashboard itself (this is separate from anything Anycubic-related — it's just to keep your own dashboard private on your network).

Settings are stored in `data/settings.json` inside the `publish` folder (gitignored, never committed). If you ever rebuild with `dotnet publish` again into the same `publish` folder, that's safe and won't touch your settings — just don't delete the folder first.

## Known limitations

- Currently Windows-only, due to the bundled `ffmpeg.exe` dependency for the camera stream. A Linux build would need its own ffmpeg binary and a small path change in `CameraStreamHandler.cs`.
- This reverse-engineers an undocumented protocol. Anycubic firmware updates could change or break it at any time without warning.
- Only tested against a Kobra 3 Max. Other Kobra 3-generation printers (K3, KS1, KS1 Max) use the same protocol family per the documented model IDs, but haven't been personally verified.
- No raw G-code injection (homing, arbitrary M/G-codes) — Anycubic's stock LAN protocol simply doesn't expose that. It only exists via Rinkhals' Moonraker bridge on custom firmware.

## Credit / prior art

The protocol groundwork here was independently reverse-engineered, then cross-checked against and refined using:
- [rvanderp3/kobra-connect](https://github.com/rvanderp3/kobra-connect) — the most complete public MQTT command reference found
- [SlimQuiggle/KobraCache](https://github.com/SlimQuiggle/KobraCache) — confirmed the file list/delete command shape independently, working live against a Kobra S1

## License

MIT — see [LICENSE](LICENSE).
