<#
.SYNOPSIS
    Release orchestrator: verifies clean state, syncs with remote, tags HEAD, publishes.

.DESCRIPTION
    1. Aborts when the working tree has uncommitted/untracked changes.
    2. Only runs on branches: main or develop.
    3. Pushes the current branch head to its remote (with upstream setup) when ahead.
    4. Determines the release version:
         - -Version <x.y.z[-pre]>      explicit (wins)
         - otherwise derived from git: last tag, patch+1
       and applies the branch flavor: develop -> "-beta", main -> plain.
       Creates annotated tag v<version> on HEAD and pushes the TAG.
    5. Runs build.ps1 -Action Publish (installer is named from that tag).

.PARAMETER Version
    Explicit version to tag (e.g. 1.2.0 or 1.2.0-beta.2). When omitted, the next version is
    derived from git and flavored by branch (develop => beta).

.EXAMPLE
    .\build_publish.ps1                        # auto version: beta on develop / plain on main
.EXAMPLE
    .\build_publish.ps1 -Version 1.2.0         # force an exact version
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = '',

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$Runtime = "win-x64",

    [Parameter(Mandatory = $false)]
    [string]$Framework = "net10.0-windows10.0.19041.0",

    [Parameter(Mandatory = $false)]
    [bool]$SelfContained = $false,

    # Print what would happen without pushing/tagging/publishing.
    [Parameter(Mandatory = $false)]
    [switch]$DryRun,

    [Alias('help')]
    [Parameter(Mandatory = $false)]
    [switch]$ShowHelp
)

$ErrorActionPreference = "Stop"

function Show-Help
{
    Write-Host @"
========================================
 Desktop Boxes - release publisher
========================================

 USAGE
   build_publish.ps1 [-Version <x.y.z[-pre]>] [-DryRun] [build params]

 WHAT IT DOES
   1. Aborts on uncommitted/untracked changes
   2. Requires branch main or develop
   3. Pushes branch head to remote (if ahead)
   4. Tags HEAD:  -Version wins, else git-derived sequential version
                  (develop -> '-beta', main -> plain)   ... then pushes the TAG
   5. Runs build.ps1 -Action Publish

 PARAMETERS
   -Version        <x.y.z[-pre]>   explicit tag version (default: auto)
   -Configuration  <Debug|Release>
   -Runtime        <rid>
   -Framework      <tfm>
   -SelfContained  <true|false>
   -DryRun                         print plan, execute nothing
"@
}

function Abort([string]$Reason)
{
    Write-Host "ABORT: $Reason" -ForegroundColor Red
    exit 1
}

function Run-Git([string]$ArgsList)
{
    # Emits git's output; callers read the automatic $LASTEXITCODE afterwards.
    # (Deliberately does NOT return the exit code — appending it here polluted output streams,
    # which made a CLEAN tree look dirty.)
    & git @($ArgsList -split ' ') 2>&1
}

if ($ShowHelp)
{
    Show-Help
    exit 0
}

$exeName = "DesktopBoxesUI.exe"

# --- 1. Clean working tree -------------------------------------------------
$status = (Run-Git "status --porcelain") -join "`n"
if ($LASTEXITCODE -ne 0) { Abort "not a git repository." }
if ($status.Trim().Length -gt 0)
{
    Abort "uncommitted/untracked changes present:`n$status`nCommit (or ignore) them first."
}
Write-Host "✓ Working tree clean" -ForegroundColor Green

# --- 2. Branch must be main/develop ----------------------------------------
$branch = (& git rev-parse --abbrev-ref HEAD) -join ''
if ($branch -notin @('main', 'develop'))
{
    Abort "current branch '$branch' is not main or develop."
}
Write-Host "✓ Branch: $branch" -ForegroundColor Green

# --- 3. Push branch head if needed -----------------------------------------
$null = Run-Git "remote" # ensure git ok
$hasUpstream = $null -ne ((& git rev-parse --abbrev-ref '@{u}' 2>$null) -join '')
$ahead = 0
if ($hasUpstream)
{
    $aheadStr = (& git rev-list --count '@{u}..HEAD') -join ''
    $ahead = [int]$aheadStr
}
else
{
    $ahead = 1 # nothing published yet
}

if ($DryRun) { Write-Host "[dry-run] branch head: ahead=$ahead upstream=$hasUpstream" -ForegroundColor Yellow }

if ($ahead -gt 0 -or -not $hasUpstream)
{
    if ($DryRun)
    {
        Write-Host "[dry-run] would push branch: git push -u origin $branch" -ForegroundColor Yellow
    }
    else
    {
        Write-Host "Pushing branch $branch ..." -ForegroundColor Yellow
        $pushOut = (& git push -u origin $branch) -join "`n"
        if ($LASTEXITCODE -ne 0) { Abort "branch push failed:`n$pushOut" }
        Write-Host $pushOut
        Write-Host "✓ Branch pushed" -ForegroundColor Green
    }
}
else
{
    Write-Host "✓ Branch already up to date" -ForegroundColor Green
}

# --- 4. Resolve version + tag HEAD ------------------------------------------
# Sequential scheme: next version = nearest tag's numeric core with PATCH+1
# (v1.0.0 -> 1.0.1; v1.0.4-beta -> 1.0.5). Override anytime with -Version.
$computed = $null
$lastTag = (& git describe --tags --abbrev=0 2>$null) -join ''
if ($LASTEXITCODE -eq 0 -and $lastTag)
{
    $base = $lastTag.TrimStart('v') -replace '-.*$', ''   # strip prerelease label
    $parts = $base.Split('.')
    if ($parts.Count -ge 3)
    {
        $parts[2] = [string]([int]$parts[2] + 1)
        $computed = $parts -join '.'
    }
}

if (-not $computed)
{
    Abort "no version tag found to derive from. Pass -Version explicitly (e.g. -Version 1.0.1)."
}

# Branch flavor when auto-computing (an explicit -Version always wins as typed).
if (-not $Version)
{
    $Version = if ($branch -eq 'develop') { "$computed-beta" } else { "$computed" }
}
elseif ($Version -notmatch '-')
{
    # explicit plain version keeps branch flavor semantics predictable: main plain,
    # develop gets -beta unless caller already added a prerelease segment.
    if ($branch -eq 'develop') { $Version = "$Version-beta" }
}

$tagName = "v$Version"

# Already tagged?
$null = & git rev-parse -q --verify "refs/tags/$tagName"
if ($LASTEXITCODE -eq 0)
{
    Abort "tag $tagName already exists. Bump the version (or delete the tag)."
}

if ($DryRun)
{
    Write-Host "[dry-run] would tag HEAD as $tagName and push the tag" -ForegroundColor Yellow
    Write-Host "[dry-run] would run: build.ps1 -Action Publish" -ForegroundColor Yellow
    exit 0
}

Write-Host "Tagging HEAD as $tagName ..." -ForegroundColor Yellow
& git tag -a $tagName -m $Version
if ($LASTEXITCODE -ne 0) { Abort "tag creation failed." }

Write-Host "Pushing tag $tagName ..." -ForegroundColor Yellow
$tagPush = (& git push origin $tagName) -join "`n"
if ($LASTEXITCODE -ne 0) { Abort "tag push failed:`n$tagPush" }
Write-Host "✓ Tag $tagName pushed" -ForegroundColor Green

# --- 5. Publish --------------------------------------------------------------
Write-Host "`nPublishing..." -ForegroundColor Cyan
& "$PSScriptRoot\build.ps1" -Action Publish `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -Framework $Framework `
    -SelfContained $SelfContained

if ($LASTEXITCODE -ne 0)
{
    Abort "publish failed."
}


