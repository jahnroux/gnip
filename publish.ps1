# Publishes self-contained, single-file build(s) of gnip for a given runtime.
#   .\publish.ps1                      # win-x64 (default) -> service + tray
#   .\publish.ps1 -Runtime linux-x64   # Linux server (service only; the tray is Windows-only)
# Output (bin\publish\<rid>) holds the binary(ies) + wwwroot\ + appsettings.json.
# xcopy-deploy the whole folder; no .NET install needed on the target.
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "bin\publish\$Runtime"

dotnet publish (Join-Path $PSScriptRoot "gnip.csproj") `
  -c Release -r $Runtime --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o $out

# The tray app is Windows-only; publish it alongside the service on win-* runtimes.
if ($Runtime -like "win*") {
  $tray = Join-Path $PSScriptRoot "tray\GnipTray.csproj"
  if (Test-Path $tray) {
    dotnet publish $tray `
      -c Release -r $Runtime --self-contained `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
      -o $out
  }
}

$bin = if ($Runtime -like "win*") { "gnip.exe" } else { "gnip" }
Write-Host ""
Write-Host "Published $Runtime -> $out"
Write-Host "  Service: $out\$bin"
if ($Runtime -like "win*") { Write-Host "  Tray   : $out\GnipTray.exe" }
