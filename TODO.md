# Kobra LAN Monitor — TODO

- **Stop-print button on the status view.** Slicer Next's own workbench has one; Kobra LAN
  Monitor doesn't. Flagged 29/09/2026 while testing Kobra Slicer's upload-and-print — had to
  stop a test print with no way to do it from the dashboard itself. The real MQTT command for
  this is already known (`print/report` action moves through `"stopping"` → `"failed"` with
  `code:10111`/"task ended abnormally" on a normal user-initiated stop, confirmed via a real
  capture) — just needs a button wired to it and a confirm-are-you-sure prompt, matching the
  existing style of other status-view controls. Not urgent — Emergency Stop already covers
  the "I need this to stop right now" case, it's just a blunter tool: Emergency Stop locks
  the printer (needs a reset/restart after), where a real Stop would cleanly return it to
  idle, ready for the next print immediately. Worth building for the cleaner behaviour, not
  because the current fallback is broken.

- ~~**Show elapsed print time, not just remaining/ETA.**~~ Done 03/10/2026. Turned out the
  printer's own report already carries this — `print_time` sits right alongside `remain_time`
  in the same `project` object, same minutes unit, confirmed against a real capture (Sheep
  print: climbed 1 per report cycle from 0 up to 817 as progress went 0% → 100%, `remain_time`
  hitting 0 at the exact same point). No timestamp-tracking needed, just read the field that
  was already being sent. New "Elapsed" stat sits next to Remaining on the dashboard.
