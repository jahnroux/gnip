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

Write-Host ""
Write-Host ("Release staged: {0}  ({1:N1} MB)" -f $dest, ((Get-Item $dest).Length / 1MB)) -ForegroundColor Green
Write-Host "Distribute via GitHub Releases:  gh release create v$display `"$dest`"" -ForegroundColor DarkGray
