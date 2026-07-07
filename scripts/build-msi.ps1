# Builds the gnip MSI installer: publishes the app, then runs WiX.
# One-time prerequisites (free WiX 5 toolset):
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.Util.wixext/5.0.2
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot "publish.ps1")            # -> bin\publish\win-x64 (service + tray)
$pub = Join-Path $root "bin\publish\win-x64"
$lic = Join-Path $root "installer\license.rtf"
$msi = Join-Path $root "bin\gnip.msi"
wix build (Join-Path $root "installer\gnip.wxs") `
  -arch x64 -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext `
  -d PublishDir=$pub -d LicenseRtf=$lic -o $msi
Write-Host ""
Write-Host "Built $msi"
