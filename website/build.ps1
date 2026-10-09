<#
.SYNOPSIS
    Stages the Maktaby landing page into website/dist for publishing.

.DESCRIPTION
    The site source lives in website/ (index.html + robots.txt + sitemap.xml +
    assets/css + assets/js + assets/img). Every screenshot is referenced from
    .github/assets instead of being duplicated, so the README and the website can
    never show different captures of the same build. This script merges the two
    into a single self-contained dist/ folder:

        website/dist/index.html          <- website/index.html
        website/dist/robots.txt          <- website/robots.txt
        website/dist/sitemap.xml         <- website/sitemap.xml
        website/dist/assets/**           <- website/assets/**
        website/dist/img/**              <- .github/assets/**

    The same script runs locally (to preview) and in .github/workflows/pages.yml
    (to publish), so the published output is whatever this script produced.

.PARAMETER OutDir
    Destination folder. Defaults to website/dist. Wiped and recreated each run.

.EXAMPLE
    pwsh -File website/build.ps1
    pwsh -File website/build.ps1 -OutDir website/dist
#>
[CmdletBinding()]
param(
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'

$siteRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $siteRoot
$sharedAssets = Join-Path $repoRoot '.github\assets'
if (-not $OutDir) { $OutDir = Join-Path $siteRoot 'dist' }

Write-Host "Staging site into $OutDir"

if (Test-Path -LiteralPath $OutDir) {
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# 1. The hand-written site: index.html, robots.txt, sitemap.xml and assets/** (css, js, img).
#    Listed explicitly so build.ps1 and dist/.gitignore never end up in the output.
foreach ($file in @('index.html', 'robots.txt', 'sitemap.xml')) {
    $src = Join-Path $siteRoot $file
    if (-not (Test-Path -LiteralPath $src)) { throw "Missing site file: $src" }
    Copy-Item -LiteralPath $src -Destination $OutDir -Force
}
$siteAssetsOut = Join-Path $OutDir 'assets'
New-Item -ItemType Directory -Path $siteAssetsOut -Force | Out-Null
Copy-Item -Path (Join-Path $siteRoot 'assets\*') -Destination $siteAssetsOut -Recurse -Force

# 2. Screenshots, icons and the README hero banner, flattened into img/.
$imgDir = Join-Path $OutDir 'img'
New-Item -ItemType Directory -Path $imgDir -Force | Out-Null
Get-ChildItem -LiteralPath $sharedAssets -File |
    Where-Object { $_.Extension -in '.webp', '.svg', '.png', '.jpg', '.jpeg' } |
    Copy-Item -Destination $imgDir -Force

# 3. GitHub Pages still runs Jekyll on a branch-published site unless it sees a
#    .nojekyll file. Nothing here needs it, but a future asset named _foo would
#    otherwise be silently dropped, so opt out explicitly.
New-Item -ItemType File -Path (Join-Path $OutDir '.nojekyll') -Force | Out-Null

# --- Report ---------------------------------------------------------------
$files = Get-ChildItem -LiteralPath $OutDir -Recurse -File
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("Staged {0} files ({1:N0} KB):" -f $files.Count, ($bytes / 1KB))
$files | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($OutDir.Length).TrimStart('\', '/')
    '  {0,-46} {1,8:N1} KB' -f $relative, ($_.Length / 1KB)
}