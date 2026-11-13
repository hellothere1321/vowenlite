# Installs VowenLite for the current user (no admin): copies the build to %LOCALAPPDATA%\VowenLite,
# registers a logon task that runs the watcher (which keeps the hotkey daemon alive), and starts it.
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'out'
$dst = Join-Path $env:LOCALAPPDATA 'VowenLite'
if (-not (Test-Path "$src\VowenLite.exe")) { & "$PSScriptRoot\build.ps1" }

Stop-ScheduledTask -TaskName 'VowenLite' -ErrorAction SilentlyContinue
# Twice: a watcher killed after its daemon may have restarted it in between.
foreach ($i in 1..2) { Get-Process VowenLite -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 700 }
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$src\*" $dst -Recurse -Force

# Model: prefer our copy; if it's missing, take Vowen's if that's still around.
$model = Join-Path $dst 'models\ggml-base.en.bin'
$vowenModel = Join-Path $env:APPDATA 'Vowen\models\ggml-base.en.bin'
if (-not (Test-Path $model) -and (Test-Path $vowenModel)) { Copy-Item $vowenModel $model }
if (-not (Test-Path $model)) { Write-Warning "No model at $model; transcription won't work until one is there." }

$exe = Join-Path $dst 'VowenLite.exe'
# Older installs started the daemon from the Run key; the logon task below replaces that.
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'VowenLite' -ErrorAction SilentlyContinue

# At logon, start the watcher (--watch), which runs the daemon and restarts it as soon as it exits.
$action = New-ScheduledTaskAction -Execute $exe -Argument '--watch' -WorkingDirectory $dst
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName 'VowenLite' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

# Start menu entries so Windows search finds them (type "planner" or "vowenlite" and press Enter).
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$ws = New-Object -ComObject WScript.Shell
foreach ($sc in @(@{ n = 'VowenLite Planner'; a = '--plan'; d = 'Tasks and calendar' }, @{ n = 'VowenLite'; a = ''; d = 'Meetings and notes' })) {
    $lnk = $ws.CreateShortcut((Join-Path $programs ($sc.n + '.lnk')))
    $lnk.TargetPath = $exe; $lnk.Arguments = $sc.a; $lnk.WorkingDirectory = $dst; $lnk.IconLocation = "$exe,0"; $lnk.Description = $sc.d
    $lnk.Save()
}

Start-ScheduledTask -TaskName 'VowenLite'
Write-Host "Installed to $dst. Press Ctrl+`` to open VowenLite, again to close it."
