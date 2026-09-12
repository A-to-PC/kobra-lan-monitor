Set fso   = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
strDir = fso.GetParentFolderName(WScript.ScriptFullName)

' --- Start the self-contained exe hidden (window style 0 = no console window on the
' taskbar) --- it's a persistent local web server meant to keep running in the
' background, not something the user babysits in a console window.
' No --HttpPort argument passed: Program.cs's own built-in fallback default is 8899,
' so there's no need to hardcode the port in two places (the exe's default AND here).
shell.CurrentDirectory = strDir
shell.Run """" & strDir & "\KobraLanMonitor.exe""", 0, False

' --- Brief pause for Kestrel to start listening, then open the dashboard ---
WScript.Sleep 1500
shell.Run "http://localhost:8899/"

' --- Resolve LAN IP so the message box can show the URL other devices would use ---
Dim ipCmd
ipCmd = "powershell -NoProfile -Command " & Chr(34) & _
    "(Get-NetIPAddress -AddressFamily IPv4 | " & _
    "Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.PrefixOrigin -ne 'WellKnown' } | " & _
    "Select-Object -First 1).IPAddress" & Chr(34)
Set ps2 = shell.Exec(ipCmd)
Do While ps2.Status = 0 : WScript.Sleep 100 : Loop
ip = Trim(ps2.StdOut.ReadAll())
If ip = "" Then ip = "your-pc-ip"

MsgBox "Kobra LAN Monitor is running in the background." & vbCrLf & vbCrLf & _
       "This PC:      http://localhost:8899/" & vbCrLf & _
       "Other devices on your LAN:   http://" & ip & ":8899/" & vbCrLf & vbCrLf & _
       "The dashboard has opened in your browser." & vbCrLf & vbCrLf & _
       "To stop it, run ""Stop Kobra LAN Monitor"" from the Start Menu.", _
       vbInformation + vbSystemModal, "Kobra LAN Monitor"
