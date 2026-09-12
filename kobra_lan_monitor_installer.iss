#define AppName "Kobra LAN Monitor"
#define ShortName "Kobra LAN Monitor"
#define AppVersion "1.0.1"
#define AppPublisher "A to PC"
; No dedicated atopc.com.au page for this one -- the GitHub repo is the only confirmed
; real URL, so that's what AppPublisherURL points at rather than guessing a domain.
#define AppURL "https://github.com/A-to-PC/kobra-lan-monitor"

[Setup]
AppId={{7485E143-F474-4549-86AC-C6071493D56F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
; Now installs to the real (machine-wide) Program Files, since admin is required anyway for
; the firewall rule below. AppSettings.cs was updated to store settings.json under
; %LOCALAPPDATA%\KobraLanMonitor instead of next to the exe specifically so this is safe --
; a non-elevated process can always write there regardless of the exe's own location. This
; also resolves Inno Setup's "per-user area used with admin privileges" warning at its root.
; Deliberately {commonpf}, NOT {autopf}: the "auto" constants resolve to either the per-user
; or per-machine location depending on Setup's detected install mode at run time, and on this
; project that detection flip-flopped between runs -- one real install ended up written to
; C:\Users\<user>\AppData\Local\Programs\KobraLanMonitor with BOTH an HKCU and an HKLM
; uninstall registry entry pointing at it, plus a duplicate full set of Start Menu/Desktop
; shortcuts (one per-user, one common) referencing a directory that was never actually
; Program Files. Since PrivilegesRequired=admin already guarantees an elevated run every
; time (Setup's manifest forces the UAC prompt before any script code runs), there's no
; scenario where the per-user fallback is ever wanted -- {commonpf}/{commondesktop}/
; {commonprograms} below remove that ambiguity by hard-coding the machine-wide path.
DefaultDirName={commonpf}\KobraLanMonitor
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=KobraLanMonitor-v{#AppVersion}-Setup
; Custom icon generated (Assets\app-icon.ico -- navy/teal rounded square, bold white "K"
; over a cyan telemetry pulse line with a live-indicator dot, evoking LAN monitoring).
; Multi-resolution PNG-frame .ico (16-256px), built via GDI+ rather than pasted stock art.
SetupIconFile=Assets\app-icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersion}.0
VersionInfoCopyright=Copyright (C) 2026 A to PC. All rights reserved.
VersionInfoCompany=A to PC
; Admin required (was PrivilegesRequired=lowest) -- needed for the automatic Windows
; Firewall exception added in [Run] below. A fresh, never-before-seen self-contained exe
; gets treated by Windows Firewall as a brand new unrecognized program; without an explicit
; rule, its own network connections (the MQTT client to the printer) can get silently
; blocked even while a separately-launched, already-trusted binary like ffmpeg.exe keeps
; working -- exactly the split symptom found testing this build. One UAC prompt at install
; time is a small, normal cost for guaranteeing this actually works out of the box after.
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"

[Files]
; Unlike Pic2Merch (a PHP app whose dev root IS the deployable app, just with its
; self-contained php.exe runtime dropped alongside), this is a compiled .NET app -- the
; dev root has .cs source, bin\, obj\, .git\, and no SDK on the target machine to run any
; of that. The actual deployable payload is what "dotnet publish -c Release -r win-x64
; --self-contained true -p:PublishSingleFile=true -o publish-installer" produced: one
; self-contained KobraLanMonitor.exe (bundles the .NET 10 runtime, no SDK/runtime needed
; on the target machine) plus wwwroot\, ffmpeg.exe, and web.config. That folder is already
; exactly what was tested (setup flow, login, static files, ffmpeg path all verified
; working from it) and contains none of the source-tree clutter, so it's copied wholesale
; with no excludes needed -- no .git, no data\settings.json (that's written fresh at first
; run per AppSettings.cs; nothing under data\ was ever part of this publish output), no
; stray prior publish* folders, no *.iss/*.md/Dockerfile/docker-compose.yml dev files.
Source: "publish-installer\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

; Hidden-window Start/Stop helper scripts (same pattern as Pic2Merch's
; start-kiosk.vbs/stop-kiosk.vbs) -- kept as the single source of truth in the project
; root rather than duplicated into publish-installer, so they don't need re-copying after
; every rebuild.
Source: "start-kobra-monitor.vbs"; DestDir: "{app}"; Flags: ignoreversion
Source: "stop-kobra-monitor.vbs"; DestDir: "{app}"; Flags: ignoreversion

; App icon, copied alongside the exe so the Start Menu/Desktop shortcuts below (which point
; a VBS script's icon at the .ico rather than wscript.exe's own) keep working after install.
Source: "Assets\app-icon.ico"; DestDir: "{app}"; Flags: ignoreversion

; README -- shown via Setup's Finished-page "View README" checkbox, same as Pic2Merch's
; installer. It's markdown rather than plain text (this project never had a .txt variant),
; so it'll render as raw markdown source in Notepad -- readable, if not pretty.
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
; Start Menu -- Start + Stop side by side, plus Uninstall. Both shortcuts launch via
; wscript.exe (so the VBS runs hidden) but IconFilename overrides the icon Windows would
; otherwise show (wscript.exe's own) with the app's actual icon.
; {commonprograms} used explicitly instead of the "{group}" macro (which resolves through
; the same ambiguous {autoprograms}) -- see the [Setup] DefaultDirName comment above.
Name: "{commonprograms}\{#AppName}\Start Kobra LAN Monitor"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\start-kobra-monitor.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\app-icon.ico"; Comment: "Start {#AppName} (runs hidden in the background) -- by {#AppPublisher}"
Name: "{commonprograms}\{#AppName}\Stop Kobra LAN Monitor"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\stop-kobra-monitor.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\app-icon.ico"; Comment: "Stop {#AppName} -- by {#AppPublisher}"
; Plain "open the dashboard" shortcut -- Start's own browser-open only fires once, right
; after launch; if that tab gets closed there was previously no way back in short of
; re-running Start (which would try, harmlessly but pointlessly, to relaunch the already-
; running exe just to reopen a browser tab). Filename here is a bare http:// URL, which
; Inno Setup auto-detects and turns into a real .url Internet Shortcut instead of a .lnk --
; no VBS/process launch involved, it just reopens the tab against whatever's already running.
Name: "{commonprograms}\{#AppName}\Open Kobra LAN Monitor"; Filename: "http://localhost:8899/"; IconFilename: "{app}\app-icon.ico"; Comment: "Open the {#AppName} dashboard in your browser"
Name: "{commonprograms}\{#AppName}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

; Desktop -- same trio, all created together under the one "desktop shortcut" task
; rather than as separate opt-in checkboxes, matching Pic2Merch's convention.
Name: "{commondesktop}\Start Kobra LAN Monitor"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\start-kobra-monitor.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\app-icon.ico"; Comment: "Start {#AppName} (runs hidden in the background) -- by {#AppPublisher}"; Tasks: desktopicon
Name: "{commondesktop}\Stop Kobra LAN Monitor"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\stop-kobra-monitor.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\app-icon.ico"; Comment: "Stop {#AppName} -- by {#AppPublisher}"; Tasks: desktopicon
Name: "{commondesktop}\Open Kobra LAN Monitor"; Filename: "http://localhost:8899/"; IconFilename: "{app}\app-icon.ico"; Comment: "Open the {#AppName} dashboard in your browser"; Tasks: desktopicon

[Run]
; Firewall exception, both directions -- inbound so other LAN devices can reach the
; dashboard, outbound so the MQTT client can actually reach the printer (the exact
; connection found silently failing for a brand-new unrecognized exe during testing).
; Scoped to private/domain profiles only, not public networks. runhidden suppresses the
; console window netsh would otherwise flash; failure here shouldn't abort the install
; (a user can still add the rule manually), hence no "abortretryignore" on error.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Kobra LAN Monitor"" dir=in action=allow program=""{app}\KobraLanMonitor.exe"" enable=yes profile=private,domain"; StatusMsg: "Adding firewall exception..."; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Kobra LAN Monitor"" dir=out action=allow program=""{app}\KobraLanMonitor.exe"" enable=yes profile=private,domain"; StatusMsg: "Adding firewall exception..."; Flags: runhidden
; No VC++ Redistributable step here -- unlike Pic2Merch's bundled PHP build, a self-contained
; .NET publish bundles its own managed + native runtime and needs nothing preinstalled on
; the target machine. Launch hidden via the Start script, same as Pic2Merch's own [Run] entry.
Filename: "{sys}\wscript.exe"; Parameters: """{app}\start-kobra-monitor.vbs"""; WorkingDir: "{app}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the firewall rule on uninstall so it doesn't linger orphaned after the exe is gone.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Kobra LAN Monitor"""; Flags: runhidden

[UninstallDelete]
; Full clean removal. settings.json now lives under %LOCALAPPDATA%\KobraLanMonitor (see
; AppSettings.cs), separate from {app} (Program Files) -- both need explicit removal here,
; the second entry wouldn't be covered by deleting {app} alone. Anyone uninstalling should
; back up %LOCALAPPDATA%\KobraLanMonitor\settings.json first if they want to keep their
; printer host/login config.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\KobraLanMonitor"
