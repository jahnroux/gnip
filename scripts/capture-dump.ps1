<#
  Captures a memory dump of the (frozen) gnip service process for diagnosis, then
  restarts the service to restore monitoring. Self-elevates.

  Run from the repo root:
    .\scripts\capture-dump.ps1

  The dump is written to C:\tmp\sqlitetest\gnip-frozen.dmp. After it's captured,
  the service is restarted so collection resumes.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "gnip",
    [string]$OutFile     = "C:\tmp\sqlitetest\gnip-frozen.dmp",
    [switch]$NoRestart,
    [switch]$Relaunched
)
$ErrorActionPreference = "Stop"

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host "Requesting elevation..." -ForegroundColor Yellow
    $a = @("-NoProfile","-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`"",
           "-ServiceName","`"$ServiceName`"","-OutFile","`"$OutFile`"","-Relaunched")
    if ($NoRestart) { $a += "-NoRestart" }
    try { $p = Start-Process powershell -Verb RunAs -ArgumentList $a -Wait -PassThru; exit $p.ExitCode }
    catch { Write-Host "Elevation cancelled." -ForegroundColor Red; exit 1 }
}

function Done($code) { if ($Relaunched) { Read-Host "`nPress Enter to close" }; exit $code }

$dumpExe = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-dump.exe"
if (-not (Test-Path $dumpExe)) {
    # fall back to whatever is on PATH
    $c = Get-Command dotnet-dump -ErrorAction SilentlyContinue
    if ($c) { $dumpExe = $c.Source } else { Write-Host "dotnet-dump not found. Install: dotnet tool install -g dotnet-dump" -ForegroundColor Red; Done 1 }
}

# Find the running gnip service process id.
$svcPid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId
if (-not $svcPid -or $svcPid -eq 0) {
    Write-Host "Service '$ServiceName' has no running process (ProcessId=$svcPid)." -ForegroundColor Red; Done 1
}
Write-Host "gnip service PID: $svcPid" -ForegroundColor Cyan

$dir = Split-Path $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

Write-Host "Collecting dump -> $OutFile (this takes a few seconds)..."
& $dumpExe collect -p $svcPid -o $OutFile --type Full
if (-not (Test-Path $OutFile) -or (Get-Item $OutFile).Length -lt 1MB) {
    Write-Host "Dump did not capture (file missing/too small). Leaving the service as-is so we can retry." -ForegroundColor Red
    Done 1
}
Write-Host ("Dump captured: {0:N0} MB" -f ((Get-Item $OutFile).Length/1MB)) -ForegroundColor Green

if ($NoRestart) {
    Write-Host "Skipping restart (-NoRestart). Service is still frozen." -ForegroundColor Yellow
    Done 0
}

Write-Host "Restarting $ServiceName to restore monitoring..."
try { Restart-Service $ServiceName -Force -ErrorAction Stop } catch { Write-Host "  restart: $($_.Exception.Message)" -ForegroundColor Yellow }
Start-Sleep -Milliseconds 1000
Write-Host "  status: $((Get-Service $ServiceName).Status)" -ForegroundColor Green
Write-Host "Dashboard: http://localhost:5099"
Done 0
