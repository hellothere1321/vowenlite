# Builds VowenLite into .\out (exe + bin\ whisper + models\), then runs the self test.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'
dotnet publish "$PSScriptRoot\VowenLite.csproj" -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Copy-Item "$PSScriptRoot\vendor\bin" $out -Recurse -Force
# Models are ~600 MB: hard-link them from vendor\models instead of a second copy (out\ still runs standalone;
# install.ps1 copies real files). Falls back to a copy if hard links are not possible.
New-Item -ItemType Directory "$out\models" -Force | Out-Null
foreach ($m in Get-ChildItem "$PSScriptRoot\vendor\models" -File) {
    $dst = Join-Path "$out\models" $m.Name
    $have = Get-Item $dst -ErrorAction SilentlyContinue
    if ($have -and $have.LinkType -eq 'HardLink' -and $have.Length -eq $m.Length) { continue }
    Remove-Item $dst -Force -ErrorAction SilentlyContinue
    try { New-Item -ItemType HardLink -Path $dst -Target $m.FullName -ErrorAction Stop | Out-Null }
    catch { Copy-Item $m.FullName $dst -Force }
}
# WinExe has no console, so pipe through Out-Host to wait for the exit code.
& "$out\VowenLite.exe" --selftest | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Self test failed" }
Write-Host "Built $out\VowenLite.exe"
