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
    5. Optionally runs build.ps1 -Action Publish to produce the local installer.
       Pushing the tag already triggers the GitHub Actions release workflow, so the local
       build is usually redundant: it is offered INTERACTIVELY after the tag is pushed, and
       is skipped entirely when there is no console to ask on.

.PARAMETER Version
    Explicit version to tag (e.g. 1.2.0 or 1.2.0-beta.2). When omitted, the next version is
    derived from git and flavored by branch (develop => beta).

.PARAMETER BuildInstaller
    Always build the installer locally; do not ask. Mutually exclusive with -SkipInstaller.

.PARAMETER SkipInstaller
    Never build the installer; tag and push only. This is the default whenever there is no
    interactive console (CI, a pipeline, a scheduled run), so the script never hangs.

.EXAMPLE
    .\build_publish.ps1                        # auto version: beta on develop / plain on main
.EXAMPLE
    .\build_publish.ps1 -Version 1.2.0         # force an exact version
.EXAMPLE
    .\build_publish.ps1 -SkipInstaller         # tag+push only (same as CI's job)
.EXAMPLE
    .\build_publish.ps1 -BuildInstaller        # tag+push and build, without the prompt
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

    # Build the local installer without being asked.
    [Parameter(Mandatory = $false)]
    [switch]$BuildInstaller,

    # Never build the local installer (tag + push only).
    [Parameter(Mandatory = $false)]
    [switch]$SkipInstaller,

    # Print what would happen without pushing/tagging/publishing.
    [Parameter(Mandatory = $false)]
    [switch]$DryRun,

    [Alias('help')]
    [Parameter(Mandatory = $false)]
    [switch]$ShowHelp
)

$ErrorActionPreference = "Stop"

if ($BuildInstaller -and $SkipInstaller)
{
    Write-Host "ABORT: -BuildInstaller and -SkipInstaller contradict each other." -ForegroundColor Red
    exit 1
}

function Show-Help
{
    Write-Host @"
========================================
 Maktaby - release publisher
========================================

 USAGE
   build_publish.ps1 [-Version <x.y.z[-pre]>] [-DryRun] [build params]

 WHAT IT DOES
   1. Aborts on uncommitted/untracked changes
   2. Requires branch main or develop
   3. Pushes branch head to remote (if ahead)
   4. Tags HEAD:  -Version wins, else the next version CONTINUES the last tag's
                  series (stable or beta) and is flavored by branch:
                    develop: 1.0.32-beta.1 -> 1.0.32-beta.2   (same core)
                             1.0.32       -> 1.0.33-beta.1   (new series)
                    main:    1.0.32-beta.5 -> 1.0.32          (promote)
                             1.0.32       -> 1.0.33          (next patch)
                  Then pushes the TAG.
                  If HEAD is already tagged with a matching flavor, the existing
                  tag is REUSED (nothing new is created).
   5. Optionally builds the installer: asks, after the tag is pushed. CI already
                  builds and publishes it from that tag, so the answer defaults to No.
                  -BuildInstaller forces yes, -SkipInstaller forces no, and with no
                  console to ask on it is skipped (never blocks a pipeline).

 PARAMETERS
   -Version        <x.y.z[-pre]>   explicit tag version (default: auto).
                                   Use this for a deliberate major/minor cut,
                                   e.g. -Version 1.1.0
   -BuildInstaller                 build the installer without asking
   -SkipInstaller                  tag + push only, never ask
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

# True when there is a real console to ask on. Output redirection is the reliable signal:
# a piped or redirected run must never block on Read-Host, which would hang CI forever.
function Test-CanPrompt
{
    try
    {
        return [Environment]::UserInteractive -and -not [Console]::IsOutputRedirected
    }
    catch
    {
        return $false
    }
}

# The installer build is OPTIONAL, and the default answer is "no": pushing the tag already
# runs the GitHub Actions release workflow, which builds and publishes the installer with the
# same version. Building it again locally only produces a duplicate that nothing consumes,
# so the prompt is a shortcut for the cases where a local .exe is actually wanted (testing it
# before the release goes out, or working offline).
function Resolve-BuildInstaller([switch]$DryRun)
{
    if ($SkipInstaller)
    {
        return $false
    }

    if ($BuildInstaller)
    {
        return $true
    }

    if ($DryRun)
    {
        Write-Host "[dry-run] would ask whether to build the installer" -ForegroundColor Yellow
        return $false
    }

    if (-not (Test-CanPrompt))
    {
        Write-Host "✓ Skipping the local installer build (no console to ask on)." -ForegroundColor Green
        Write-Host "  CI builds and publishes the installer from the pushed tag." -ForegroundColor DarkGray
        Write-Host "  Pass -BuildInstaller to build it here anyway." -ForegroundColor DarkGray
        return $false
    }

    Write-Host ""
    Write-Host "The tag is pushed, so GitHub Actions is building the installer for $tagName." -ForegroundColor Cyan
    Write-Host "Build a local copy too?" -ForegroundColor Cyan
    $answer = Read-Host "  [y/N]"
    return $answer -match '^(y|yes)$'
}

if ($ShowHelp)
{
    Show-Help
    exit 0
}

$exeName = "Maktaby.exe"

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
# Version scheme: the branch decides the CHANNEL, and the channel is continued
# from whatever the last tag was — stable or beta — so a beta series converges on
# one number instead of crawling the patch counter.
#
#   develop, last = 1.0.32-beta       -> 1.0.32-beta.1     (start the series)
#   develop, last = 1.0.32-beta.1     -> 1.0.32-beta.2     (continue it)
#   develop, last = 1.0.32-beta.9     -> 1.0.32-beta.10    (numeric, not string)
#   develop, last = 1.0.32 (stable)   -> 1.0.33-beta.1     (next line)
#   main,    last = 1.0.32-beta.5     -> 1.0.32            (promote the series)
#   main,    last = 1.0.32 (stable)   -> 1.0.33            (next patch)
#
# A -beta line therefore never bumps the patch: 1.0.32-beta, 1.0.32-beta.1,
# 1.0.32-beta.2 ... all target 1.0.32, which is what main then ships.
# -Version still overrides everything, for deliberate major/minor cuts.
#
# REUSE RULE: when HEAD is ALREADY tagged and that tag matches the branch flavor
# (develop => name contains 'beta'; main => plain, no prerelease), NO new tag is
# created — the existing tag is used/pushed as-is. Override anytime with -Version.

$headSha = (& git rev-parse HEAD) -join ''
$lastTag = (& git describe --tags --abbrev=0 2>$null) -join ''

$tagAtHead = $false
if ($LASTEXITCODE -eq 0 -and $lastTag)
{
    $tagSha = (& git rev-parse "$lastTag^{commit}") -join ''
    $tagAtHead = ($tagSha -eq $headSha)
}

function Test-TagMatchesFlavor([string]$name, [string]$br)
{
    $core = $name.TrimStart('v')
    if ($br -eq 'develop') { return $core -match '-beta' }
    return $core -notmatch '-'     # main: plain release tags only
}

# Splits "v1.0.32-beta.2" into @{ Core = "1.0.32"; Pre = "beta.2" }.
# A stable tag yields Pre = ''. Only the first '-' is the prerelease boundary, so
# a build-metadata '+sha' suffix would stay in Pre — callers want it gone.
function Split-Version([string]$version)
{
    $core = $version.TrimStart('v')
    $plus = $core.IndexOf('+')
    if ($plus -ge 0) { $core = $core.Substring(0, $plus) }

    $dash = $core.IndexOf('-')
    if ($dash -lt 0)
    {
        return @{ Core = $core; Pre = '' }
    }
    return @{ Core = $core.Substring(0, $dash); Pre = $core.Substring($dash + 1) }
}

# PATCH+1 on the numeric core ("1.0.32" -> "1.0.33"). A pre-release label is
# discarded first: bumping the core of "1.0.32-beta.5" must yield 1.0.33, never
# 1.0.33-beta.5.
function Step-Patch([string]$core)
{
    $parts = $core.Split('.')
    if ($parts.Count -ge 3)
    {
        $parts[2] = [string]([int]$parts[2] + 1)
    }
    return ($parts -join '.')
}

# Next version for a branch, given the last tag's parsed form.
#   develop: continue a beta series in place, or open a new one from a stable tag.
#   main:    promote the beta series to its core, else patch+1 off a stable tag.
function Get-NextVersion([string]$core, [string]$pre, [string]$br)
{
    $isBetaSeries = $pre -match '^beta(\.\d+)?$'

    if ($br -eq 'develop')
    {
        # Continue an open beta series on the SAME core: 1.0.32-beta -> .1, .1 -> .2,
        # .9 -> .10. The counter is parsed as an int so ordering never becomes
        # lexicographic ("beta.10" must sort above "beta.9").
        if ($isBetaSeries)
        {
            $n = 0
            if ($pre -match '^beta\.(\d+)$') { $n = [int]$Matches[1] }
            return "$core-beta.$($n + 1)"
        }

        # No open series (last tag was stable, or some other pre-release): open the
        # next beta line. A beta series must be NEW work, so it starts on the bumped
        # core rather than reopening a version that already shipped.
        return "$(Step-Patch $core)-beta.1"
    }

    # main / stable.
    if ($pre -ne '')
    {
        # Promote: every beta in this series was shipping towards this exact core, so
        # the stable release IS the core (1.0.32-beta.5 -> 1.0.32). That is the whole
        # reason the beta series holds its core still.
        return $core
    }

    return (Step-Patch $core)
}

$createTag = $true

if ($Version)
{
    # Explicit wins; a plain value on develop gets the beta flavor appended.
    if ($branch -eq 'develop' -and $Version -notmatch '-')
    {
        $Version = "$Version-beta"
    }
}
elseif ($tagAtHead -and (Test-TagMatchesFlavor $lastTag $branch))
{
    # HEAD already carries the correct release/beta tag: reuse it, create nothing.
    $createTag = $false
    $Version = $lastTag.TrimStart('v')
    Write-Host "✓ HEAD already tagged $($lastTag) — reusing it (no new tag)." -ForegroundColor Green
}
elseif ($lastTag)
{
    $parsed = Split-Version $lastTag
    $Version = Get-NextVersion $parsed.Core $parsed.Pre $branch
}
else
{
    Abort "no version tag found to derive from. Pass -Version explicitly (e.g. -Version 1.0.1)."
}

$tagName = "v$Version"

if ($createTag)
{
    # Duplicate guard (only relevant when we intend to CREATE).
    $null = & git rev-parse -q --verify "refs/tags/$tagName"
    if ($LASTEXITCODE -eq 0)
    {
        Abort "tag $tagName already exists. Bump the version (or delete the tag)."
    }
}

if ($DryRun)
{
    if ($createTag)
    {
        Write-Host "[dry-run] would tag HEAD as $tagName and push the tag" -ForegroundColor Yellow
    }
    else
    {
        Write-Host "[dry-run] reusing existing tag $tagName (nothing to tag)" -ForegroundColor Yellow
    }
    if ($SkipInstaller -or -not $BuildInstaller)
    {
        Write-Host "[dry-run] would then ask whether to build the installer (default: no)" -ForegroundColor Yellow
    }
    else
    {
        Write-Host "[dry-run] would run: build.ps1 -Action Publish" -ForegroundColor Yellow
    }
    exit 0
}

if ($createTag)
{
    Write-Host "Tagging HEAD as $tagName ..." -ForegroundColor Yellow
    & git tag -a $tagName -m $Version
    if ($LASTEXITCODE -ne 0) { Abort "tag creation failed." }
}

Write-Host "Pushing tag $tagName ..." -ForegroundColor Yellow
$tagPush = (& git push origin $tagName) -join "`n"
if ($LASTEXITCODE -ne 0) { Abort "tag push failed:`n$tagPush" }
if ($createTag)
{
    Write-Host "✓ Tag $tagName pushed" -ForegroundColor Green
}
else
{
    Write-Host "✓ Tag $tagName already present on remote" -ForegroundColor Green
}

# --- 5. Optionally build the installer locally ---------------------------------
# Asked AFTER the tag is pushed on purpose: the irreversible git work is already done, so a
# "no" here costs nothing and the answer cannot change what was tagged.
if (-not (Resolve-BuildInstaller -DryRun:$DryRun))
{
    Write-Host ""
    Write-Host "Done. GitHub Actions is building and publishing the $tagName release." -ForegroundColor Green
    exit 0
}

Write-Host "`nPublishing installer for $tagName ..." -ForegroundColor Cyan
& "$PSScriptRoot\build.ps1" -Action Publish `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -Framework $Framework `
    -SelfContained $SelfContained

if ($LASTEXITCODE -ne 0)
{
    # Note: the tag is ALREADY pushed, so CI is building the release regardless. A local
    # publish failure is therefore not a failed release - say so instead of implying rollback.
    Write-Host ""
    Write-Host "Local publish failed, but the $tagName tag is already pushed." -ForegroundColor Red
    Write-Host "GitHub Actions is building the release anyway - check the Actions tab." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "Done. Local installer built for $tagName." -ForegroundColor Green


