# Builds VowenLite into .\out (exe + bin\ whisper + models\), then runs the self test.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'
dotnet publish "$PSScriptRoot\VowenLite.csproj" -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$PSScriptRoot\vendor\*" $out -Recurse -Force
# WinExe has no console, so pipe through Out-Host to wait for the exit code.
& "$out\VowenLite.exe" --selftest | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Self test failed" }
Write-Host "Built $out\VowenLite.exe"
