# Removes VowenLite (program, logon task, settings). Meetings in Documents\VowenLite are kept.
$ErrorActionPreference = 'Stop'
$dst = Join-Path $env:LOCALAPPDATA 'VowenLite'
Stop-ScheduledTask -TaskName 'VowenLite' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'VowenLite' -Confirm:$false -ErrorAction SilentlyContinue
# Twice: a watcher killed after its daemon may have restarted it in between.
foreach ($i in 1..2) { Get-Process VowenLite -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 700 }
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'VowenLite' -ErrorAction SilentlyContinue
foreach ($n in 'VowenLite Planner', 'VowenLite') { Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$n.lnk") -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 300
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
Write-Host "Uninstalled. Meetings are still in $([Environment]::GetFolderPath('MyDocuments'))\VowenLite\Meetings."
