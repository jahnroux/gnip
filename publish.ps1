# Publishes a self-contained, single-file build of gnip for a given runtime.
#   .\publish.ps1                      # win-x64 (default)
#   .\publish.ps1 -Runtime linux-x64   # Linux server
# Output (bin\publish\<rid>) contains the single binary + wwwroot\ + appsettings.json.
# xcopy-deploy the whole folder; no .NET install needed on the target.
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "bin\publish\$Runtime"
dotnet publish (Join-Path $PSScriptRoot "gnip.csproj") `
  -c Release -r $Runtime --self-contained `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $out
$bin = if ($Runtime -like "win*") { "gnip.exe" } else { "gnip" }
Write-Host ""
Write-Host "Published $Runtime -> $out"
Write-Host "Binary: $out\$bin"
