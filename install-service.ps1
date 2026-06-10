<#
  Install (or remove) gnip as a Windows service. Self-elevates if not run as admin.

    .\install-service.ps1                              # install from bin\publish\win-x64, localhost:5099
    .\install-service.ps1 -Url http://0.0.0.0:5099     # expose on the network (also open the firewall)
    .\install-service.ps1 -BinPath C:\gnip\gnip.exe    # install from a copied location
    .\install-service.ps1 -Uninstall                   # stop + remove the service (data is kept)

  ICMP needs no special privilege on Windows, so the default LocalSystem account is fine.
#>
param(
  [string]$ServiceName = "gnip",
  [string]$BinPath = (Join-Path $PSScriptRoot "bin\publish\win-x64\gnip.exe"),
  [string]$DataDir = "C:\ProgramData\gnip",
  [string]$Url = "http://localhost:5099",
  [switch]$Uninstall,
  [switch]$Relaunched   # internal: set when self-elevating, so the elevated window pauses before closing
)
$ErrorActionPreference = "Stop"

# --- self-elevate if needed (wait for the child and propagate its exit code) ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host "Administrator rights required - relaunching elevated..."
  $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Relaunched")
  foreach ($kv in $PSBoundParameters.GetEnumerator()) {
    if ($kv.Value -is [switch]) { if ($kv.Value.IsPresent) { $argList += "-$($kv.Key)" } }
    else { $argList += "-$($kv.Key)"; $argList += "`"$($kv.Value)`"" }
  }
  try {
    $proc = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru
    exit $proc.ExitCode
  }
  catch {
    Write-Warning "Elevation was declined; nothing was changed."
    exit 1
  }
}

function Wait-ServiceGone([string]$name, [int]$timeoutSec = 15) {
  # sc.exe delete only MARKS the service for deletion; wait until it's actually gone
  # so a same-name New-Service doesn't fail with 1072 ('marked for deletion').
  $deadline = (Get-Date).AddSeconds($timeoutSec)
  while (Get-Service -Name $name -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) {
      throw "Service '$name' is still marked for deletion. Close the Services console and the GnipTray app, then re-run."
    }
    Start-Sleep -Milliseconds 250
  }
}

function Remove-Existing {
  $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
  if (-not $svc) { return }
  Write-Host "Stopping and removing existing '$ServiceName'..."
  if ($svc.Status -ne 'Stopped') {
    try { Stop-Service -Name $ServiceName -Force -ErrorAction Stop }
    catch { Write-Warning "Could not stop '$ServiceName' cleanly: $($_.Exception.Message)" }
  }
  & sc.exe delete $ServiceName | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Warning "sc.exe delete returned exit code $LASTEXITCODE." }
  Wait-ServiceGone $ServiceName
}

$exitCode = 0
try {
  if ($Uninstall) {
    Remove-Existing
    Write-Host "Removed service '$ServiceName'. Data in $DataDir was left intact."
  }
  else {
    if (-not (Test-Path $BinPath)) {
      throw "gnip.exe not found at '$BinPath'. Run .\publish.ps1 first, or pass -BinPath."
    }
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    Remove-Existing

    # New-Service handles quoting of the exe path + args cleanly (unlike raw 'sc create').
    $imagePath = "`"$BinPath`" --urls $Url --Gnip:DbPath=`"$DataDir\gnip.db`""
    New-Service -Name $ServiceName `
      -BinaryPathName $imagePath `
      -DisplayName "gnip latency monitor" `
      -Description "gnip - PingPlotter-style latency/loss monitor" `
      -StartupType Automatic | Out-Null

    # Auto-restart on crash: 5s, 5s, then 15s; reset the counter daily.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/15000 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "sc.exe failure returned exit code $LASTEXITCODE; crash-recovery may not be configured." }

    # Register the Windows Event Log source so the service's logging provider finds it instead
    # of throwing FileNotFoundException trying to create it lazily on the first write.
    try {
      if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
        New-EventLog -LogName Application -Source $ServiceName -ErrorAction Stop
        Write-Host "  Registered Event Log source '$ServiceName'."
      }
    } catch { Write-Warning "Could not register Event Log source '$ServiceName': $($_.Exception.Message)" }

    Start-Service -Name $ServiceName

    Write-Host ""
    Write-Host "Installed and started '$ServiceName'."
    Write-Host "  Dashboard : $Url"
    Write-Host "  Data dir  : $DataDir"
    Write-Host "  Manage    : the GnipTray system-tray app, or 'sc start|stop $ServiceName'"
    Get-Service -Name $ServiceName | Format-Table -AutoSize
  }
}
catch {
  Write-Warning "Operation failed: $($_.Exception.Message)"
  $exitCode = 1
}
finally {
  if ($Relaunched) { Read-Host "`nPress Enter to close" | Out-Null }
}
exit $exitCode
