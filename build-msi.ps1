# Builds the gnip MSI installer: publishes the app, then runs WiX.
# One-time prerequisites (free WiX 5 toolset):
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.Util.wixext/5.0.2
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "publish.ps1")            # -> bin\publish\win-x64 (service + tray)
$pub = Join-Path $PSScriptRoot "bin\publish\win-x64"
$lic = Join-Path $PSScriptRoot "installer\license.rtf"
$msi = Join-Path $PSScriptRoot "bin\gnip.msi"
wix build (Join-Path $PSScriptRoot "installer\gnip.wxs") `
  -arch x64 -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext `
  -d PublishDir=$pub -d LicenseRtf=$lic -o $msi
Write-Host ""
Write-Host "Built $msi"
