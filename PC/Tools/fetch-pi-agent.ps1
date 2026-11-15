# Refresh the vendored pi runtime under ThirdParty/PiAgent and rewrite manifest.json.
#
# Usage:
#   pwsh -File Tools/fetch-pi-agent.ps1 -PackageRoot 'D:\npm-global\node_modules\@earendil-works\pi-coding-agent'
#
# Parameters:
#   -PackageRoot  installed @earendil-works/pi-coding-agent directory (contains dist and package.json)
#   -Version      version label to record; defaults to the package's own version
#
# Copied set:
#   package.json, dist/bundle, dist/modes/interactive/{theme,assets}, dist/core/export-html,
#   and node_modules/jiti.
#
# Why the layout is preserved: pi resolves the package root through package.json and reads the
# TUI theme and HTML export template by relative path, so flattening dist/ makes it fail at
# startup with ENOENT. Why jiti is copied: pi loads extensions through require("jiti") at
# runtime, and our approval extension is one of them.
#
# Source maps and .d.ts files are not copied. The script does not go online and does not upgrade
# anything: the version is pinned and upgrading is a deliberate manual act.
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads a BOM-less script as ANSI,
# so non-ASCII text here would silently turn into mojibake.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repoRoot 'ThirdParty\PiAgent'
$sourceRoot = Join-Path $PackageRoot 'dist'

if (-not (Test-Path $sourceRoot)) {
    throw "Not found: $sourceRoot. Point -PackageRoot at an installed pi-coding-agent package."
}

$packageJson = Join-Path $PackageRoot 'package.json'
$upstream = Get-Content $packageJson -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $upstream.version }

Write-Host "upstream version: $Version"
Write-Host "target directory: $target"

$paths = @(
    @{ From = 'dist\bundle'; To = 'dist\bundle' },
    @{ From = 'dist\modes\interactive\theme'; To = 'dist\modes\interactive\theme' },
    @{ From = 'dist\modes\interactive\assets'; To = 'dist\modes\interactive\assets' },
    @{ From = 'dist\core\export-html'; To = 'dist\core\export-html' },
    @{ From = 'node_modules\jiti'; To = 'node_modules\jiti' }
)

New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem $target -Exclude 'README.md', 'THIRD-PARTY-NOTICES.md' |
    Remove-Item -Recurse -Force

foreach ($entry in $paths) {
    $from = Join-Path $PackageRoot $entry.From
    $to = Join-Path $target $entry.To
    if (-not (Test-Path $from)) { throw "Not found: $from" }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Copy-Item (Join-Path $from '*') -Destination $to -Recurse -Force
}

Get-ChildItem $target -Recurse -File -Include '*.map' | Remove-Item -Force
Copy-Item $packageJson (Join-Path $target 'package.json') -Force

$today = Get-Date -Format 'yyyy-MM-dd'
$files = Get-ChildItem $target -Recurse -File |
    Where-Object { $_.Name -notin @('manifest.json', 'README.md', 'THIRD-PARTY-NOTICES.md') } |
    Sort-Object FullName

$entries = foreach ($file in $files) {
    $relative = $file.FullName.Substring($target.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "    {{ ""path"": ""{0}"", ""sha256"": ""{1}"", ""bytes"": {2} }}" -f $relative, $hash, $file.Length
}

$manifest = @"
{
  "package": "$($upstream.name)",
  "version": "$Version",
  "license": "$($upstream.license)",
  "vendoredAt": "$today",
  "entry": "dist/bundle/cli.js",
  "note": "Upstream runtime files copied verbatim (source maps omitted); jiti is vendored because pi loads extensions through it at runtime. Refresh with Tools/fetch-pi-agent.ps1.",
  "files": [
$($entries -join ",`n")
  ]
}
"@

Set-Content -Path (Join-Path $target 'manifest.json') -Value $manifest -Encoding UTF8

$count = ($files | Measure-Object).Count
$sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Copied $count files ($sizeMb MB) and rewrote manifest.json."
Write-Host "Update the version notes in ThirdParty/PiAgent/README.md, then run Tests/FairyCode."
