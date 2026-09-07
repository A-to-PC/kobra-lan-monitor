# Kobra LAN Monitor

A self-hosted web dashboard for the Anycubic Kobra 3 (and likely other Kobra 3-generation printers), built by fully reverse-engineering Anycubic's local LAN protocol — no cloud account, no Anycubic app, no third-party relay. It talks directly to your printer over your own network.

Live status, camera streaming, print control, ACE (multi-material) filament and drying control, and remote file/print management, all from a browser on any device on your LAN.

## Features

**Live-verified against a real printer:**
- Live status: state, progress, layer, ETA, nozzle/bed temps, fan, filament slots
- Genuine continuous camera stream (MJPEG, low latency — no polling)
- Pause / Resume / Emergency Stop
- File browser (local storage + USB): list, folder navigation, delete
- ACE box: filament colours/types, active-slot indicator, drying on/off

**Built from the documented protocol, not yet fully proven against real hardware:**
- Start a print from a file already on the printer (partially confirmed — command reaches the printer and it begins executing, but a clean full test is still pending)
- Light on/off + brightness
- Nozzle/bed temperature, fan speed, and print-speed-mode adjustment mid-print
- File preview thumbnails
- Upload a file from your PC straight to the printer (`/gcode_upload`) and print it

If you try any of the second group and it doesn't work exactly as expected, please open an issue with what you saw — that's exactly the kind of feedback this needs before those get promoted to "confirmed."

## Why this exists

Anycubic's own apps (Slicer Next, the Anycubic Cloud app) require a cloud account and don't expose everything the printer's own LAN protocol actually supports. This project talks to the printer the same way those official apps do — a local HTTP handshake to discover per-session MQTT credentials, then mutual-TLS MQTT directly to the printer — just without the cloud account requirement.

Credentials are never hardcoded. Every session performs its own live handshake against your printer's IP to obtain fresh, printer-specific credentials. Nothing here embeds Anycubic's shared fleet secrets.

## Requirements

- .NET 10 SDK
- `ffmpeg.exe` (Windows build) placed in the project root — not bundled in this repo (keeps the repo small; this is a third-party binary). Grab a static Windows build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) and drop `ffmpeg.exe` next to `KobraLanMonitor.csproj`.
- Your printer must be on the same LAN, with LAN mode reachable (this is the same connection Slicer Next itself uses).

## Setup

```
dotnet publish -c Release -o publish
cd publish
dotnet KobraLanMonitor.dll --HttpPort=8090
```

Then open `http://localhost:8090` (or `http://<your-pc-ip>:8090` from another device), and complete the one-time setup form: your printer's LAN IP address, and a username/password for the dashboard itself (this is separate from anything Anycubic-related — it's just to keep your own dashboard private on your network).

Settings are stored in `data/settings.json` next to the published app (gitignored, never committed).

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
