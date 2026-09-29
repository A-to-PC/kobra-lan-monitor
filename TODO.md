# Kobra LAN Monitor — TODO

- **Stop-print button on the status view.** Slicer Next's own workbench has one; Kobra LAN
  Monitor doesn't. Flagged 29/09/2026 while testing Kobra Slicer's upload-and-print — had to
  stop a test print with no way to do it from the dashboard itself. The real MQTT command for
  this is already known (`print/report` action moves through `"stopping"` → `"failed"` with
  `code:10111`/"task ended abnormally" on a normal user-initiated stop, confirmed via a real
  capture) — just needs a button wired to it and a confirm-are-you-sure prompt, matching the
  existing style of other status-view controls.
