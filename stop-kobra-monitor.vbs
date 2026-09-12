Set shell = CreateObject("WScript.Shell")

' The self-contained exe has a unique image name (unlike Pic2Merch's shared php.exe,
' which needed a command-line/port filter to avoid killing a sibling app) so a plain
' name-based stop is safe here.
psCmd = "powershell -NoProfile -Command " & Chr(34) & _
    "$p = Get-Process -Name 'KobraLanMonitor' -ErrorAction SilentlyContinue; " & _
    "if ($p) { $p | Stop-Process -Force; 'stopped' } else { 'notfound' }" & Chr(34)
Set out = shell.Exec(psCmd)
Do While out.Status = 0 : WScript.Sleep 100 : Loop
result = Trim(out.StdOut.ReadAll())

If result = "stopped" Then
    MsgBox "Kobra LAN Monitor stopped.", vbInformation, "Kobra LAN Monitor"
Else
    MsgBox "Kobra LAN Monitor was not running (or could not be stopped).", vbExclamation, "Kobra LAN Monitor"
End If
