# Regenerates installer\license.rtf (the license page shown by the MSI setup wizard) from the
# LICENSE file, so the two stay in sync. Run after editing LICENSE:  .\scripts\make-license-rtf.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$src  = Join-Path $root "LICENSE"
$dst  = Join-Path $root "installer\license.rtf"

$out = [System.Text.StringBuilder]::new()
[void]$out.Append('{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}' + "`r`n")
[void]$out.Append('\fs18' + "`r`n")
foreach ($line in (Get-Content -LiteralPath $src)) {
    $t = $line
    $t = $t -replace '^#{1,6}\s*', ''               # drop markdown heading markers
    $t = $t -replace '\[([^\]]+)\]\(#[^)]+\)', '$1'  # [text](#anchor) -> text
    $t = $t -replace '\*\*\*', ''                    # ***emphasis*** markers
    $t = $t -replace '<(https?://[^>]+)>', '$1'      # <url> -> url
    $t = $t -replace '\\', '\\'                      # RTF-escape backslash
    $t = $t -replace '\{', '\{'                      # RTF-escape braces
    $t = $t -replace '\}', '\}'
    [void]$out.Append($t + '\par' + "`r`n")
}
[void]$out.Append('}')
Set-Content -LiteralPath $dst -Value $out.ToString() -Encoding ascii
Write-Host "Wrote $dst from LICENSE."
