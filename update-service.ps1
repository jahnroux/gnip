<#
  Updates the installed gnip service binary in place with a freshly published build.

  Use this after rebuilding (.\publish.ps1) to deploy a fix WITHOUT reinstalling the MSI.
  It self-elevates, stops the service, swaps gnip.exe, restarts it, and confirms the
  collector is writing fresh samples again. Your data in C:\ProgramData\gnip is untouched.

  Usage (from the repo root):
    .\publish.ps1          # build the new gnip.exe
    .\update-service.ps1   # stop -> swap -> start (UAC prompt)
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "gnip",
    [string]$NewExe      = (Join-Path $PSScriptRoot "bin\publish\win-x64\gnip.exe"),
    [string]$InstallDir  = "C:\Program Files\gnip",
    [string]$Url         = "http://localhost:5099",
    [switch]$Relaunched
)
$ErrorActionPreference = "Stop"

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# --- self-elevate -----------------------------------------------------------
if (-not (Test-Admin)) {
    Write-Host "Requesting elevation..." -ForegroundColor Yellow
    $argList = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"",
        "-ServiceName", "`"$ServiceName`"", "-NewExe", "`"$NewExe`"",
        "-InstallDir", "`"$InstallDir`"", "-Url", "`"$Url`"", "-Relaunched"
    )
    try {
        $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Write-Host "Elevation was cancelled." -ForegroundColor Red
        exit 1
    }
}

function Fail($msg) {
    Write-Host $msg -ForegroundColor Red
    if ($Relaunched) { Read-Host "Press Enter to close" }
    exit 1
}

$target = Join-Path $InstallDir "gnip.exe"
Write-Host "Updating installed service binary" -ForegroundColor Cyan
Write-Host "  target : $target"
Write-Host "  source : $NewExe"

if (-not (Test-Path $NewExe)) { Fail "New build not found: $NewExe  (run .\publish.ps1 first)" }
if (-not (Test-Path $target)) { Fail "Installed service exe not found: $target  (is gnip installed via the MSI?)" }

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) { Fail "Service '$ServiceName' not found." }

# --- stop (and keep it stopped long enough to swap the file) ----------------
Write-Host "Stopping $ServiceName..."
try { Stop-Service $ServiceName -Force -ErrorAction Stop } catch { Write-Host "  stop: $($_.Exception.Message)" -ForegroundColor Yellow }

$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 300
    $st = (Get-Service $ServiceName).Status
} while ($st -ne 'Stopped' -and (Get-Date) -lt $deadline)
Write-Host "  status: $st"
if ($st -ne 'Stopped') { Fail "Service did not stop in time." }

# --- swap the binary (retry in case crash-recovery raced a restart) ---------
$copied = $false
for ($i = 0; $i -lt 10; $i++) {
    try {
        Copy-Item -LiteralPath $NewExe -Destination $target -Force -ErrorAction Stop
        $copied = $true; break
    } catch {
        Write-Host "  file busy, retrying ($($i + 1))..." -ForegroundColor Yellow
        try { Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Milliseconds 700
    }
}
if (-not $copied) { Fail "Could not replace $target (still locked by a running process)." }
Write-Host "  binary replaced." -ForegroundColor Green

# Ensure the Windows Event Log source exists. The service's EventLog logging provider lazily
# tries to CREATE this source on first write; under the service that creation path threw
# FileNotFoundException, which (pre-fix) killed the collector on the first packet loss.
# Pre-creating it here means the provider finds the source and never takes the throwing path.
# Best-effort and safe to run repeatedly.
try {
    if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
        New-EventLog -LogName Application -Source $ServiceName -ErrorAction Stop
        Write-Host "  registered Event Log source '$ServiceName'." -ForegroundColor Green
    } else {
        Write-Host "  Event Log source '$ServiceName' already present." -ForegroundColor DarkGray
    }
} catch { Write-Host "  (could not register Event Log source: $($_.Exception.Message))" -ForegroundColor Yellow }

# --- start and confirm collection resumed -----------------------------------
Write-Host "Starting $ServiceName..."
Start-Service $ServiceName
Start-Sleep -Milliseconds 800
Write-Host "  status: $((Get-Service $ServiceName).Status)"

Write-Host "Waiting for fresh samples..."
$ok = $false
$deadline = (Get-Date).AddSeconds(20)
do {
    Start-Sleep -Seconds 1
    try {
        $j = (Invoke-WebRequest -UseBasicParsing "$Url/api/recent?limit=1" -TimeoutSec 4).Content | ConvertFrom-Json
        $age = ((Get-Date) - [DateTimeOffset]::FromUnixTimeMilliseconds($j[0].ts).LocalDateTime).TotalSeconds
        if ($age -lt 10) { $ok = $true; break }
    } catch {}
} while ((Get-Date) -lt $deadline)

if ($ok) { Write-Host "gnip is collecting again (fresh sample seen). Open $Url" -ForegroundColor Green }
else     { Write-Host "Updated and started, but no fresh sample seen yet - check $Url" -ForegroundColor Yellow }

if ($Relaunched) { Read-Host "Done. Press Enter to close" }
