# Builds the MSI and stages a versioned, distributable copy in releases\.
# The version is read from installer\gnip.wxs (Package Version) — the single source of truth.
# To cut a new release: bump Version in installer\gnip.wxs, then run this from the repo root:
#   .\scripts\release.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# Read the product version from the WiX package.
$wxs = Get-Content (Join-Path $root "installer\gnip.wxs") -Raw
# Case-sensitive (-cmatch): match the Package's Version="..." and NOT the lowercase
# version="1.0" in the <?xml ...?> declaration.
if ($wxs -cmatch 'Version="([0-9]+(?:\.[0-9]+){1,3})"') {
    $full = $Matches[1]                 # e.g. 1.1.0.0
} else {
    throw "Could not read the Package Version from installer\gnip.wxs"
}
$display = $full -replace '\.0$', ''    # e.g. 1.1.0  (trim a trailing .0 for a friendlier name)

Write-Host "Building gnip release $display ..." -ForegroundColor Cyan

# Build the MSI (publishes the self-contained service + tray, then runs WiX).
& (Join-Path $PSScriptRoot "build-msi.ps1")

# Stage a versioned copy for distribution.
$releases = Join-Path $root "releases"
New-Item -ItemType Directory -Force $releases | Out-Null
$dest = Join-Path $releases "gnip-$display.msi"
Copy-Item (Join-Path $root "bin\gnip.msi") $dest -Force

# Stage the portable single-file exe too. wwwroot is embedded in the assembly, so this one file
# is the whole app: no install, no admin, no sibling files. (build-msi.ps1 already published it.)
$portable = Join-Path $releases "gnip-$display-portable.exe"
Copy-Item (Join-Path $root "bin\publish\win-x64\gnip.exe") $portable -Force

Write-Host ""
Write-Host "Release $display staged:" -ForegroundColor Green
Write-Host ("  installer : {0}  ({1:N1} MB)" -f $dest,     ((Get-Item $dest).Length     / 1MB)) -ForegroundColor Green
Write-Host ("  portable  : {0}  ({1:N1} MB)" -f $portable, ((Get-Item $portable).Length / 1MB)) -ForegroundColor Green
Write-Host ""
Write-Host "Publish both via GitHub Releases:" -ForegroundColor DarkGray
Write-Host "  gh release create v$display `"$dest`" `"$portable`" --title `"gnip $display`" --notes `"...`"" -ForegroundColor DarkGray
