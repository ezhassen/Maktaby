<#
.SYNOPSIS
    Builds, publishes and packages Maktaby, and manages git-tag-driven versions.

.DESCRIPTION
    Versions come from GIT TAGS via MinVer:
      - tag v1.2.0            -> exact release 1.2.0
      - tag v1.2.0-beta.1     -> prerelease 1.2.0-beta.1 (use on develop)
      - commits after a tag   -> auto prerelease with height (e.g. 1.2.1-alpha.0.N)

.EXAMPLE
    .\build.ps1 -Action Build

.EXAMPLE
    .\build.ps1 -Action Publish

.EXAMPLE
    .\build.ps1 -Action Publish -Runtime win-arm64 -Flavor full

.EXAMPLE
    .\build.ps1 -Action Tag -Version 1.2.0-beta.1

.NOTES
    Run without arguments (or -?, -Help, Help) to see the full help text.
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet('', 'Help', 'Publish', 'Build', 'Clean', 'Version', 'Tag')]
    [string]$Action = '',

    # Version used by -Action Tag (creates annotated tag v<Version>). Example: 1.2.0-beta.1
    [Parameter(Mandatory = $false)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$Runtime = "win-x64",

    [Parameter(Mandatory = $false)]
    [string]$Framework = "net10.0-windows10.0.19041.0",

    # lean = framework-dependent (needs the .NET Desktop Runtime, small download, the
    # historical default). full = self-contained (bundles the runtime, ~3x the download,
    # nothing to install first). This one switch is the whole difference between the two
    # installer flavors, so it drives both the publish and the asset name.
    [Parameter(Mandatory = $false)]
    [ValidateSet("lean", "full")]
    [string]$Flavor = "lean",

    [Alias('help')]
    [Parameter(Mandatory = $false)]
    [switch]$ShowHelp
)

$ErrorActionPreference = "Stop"
$solutionRoot = $PSScriptRoot
$project = "$PSScriptRoot\src\Maktaby\Maktaby.csproj"

# One publish tree per (runtime, flavor). They MUST NOT share a directory: the installer
# packs whatever it finds under SourceDir recursively, so a shared folder would let a lean
# x64 publish leak into an arm64 full installer.
$publishDir = Join-Path $PSScriptRoot "src\Maktaby\bin\Publish\$Runtime-$Flavor"
$publishedExe = Join-Path $publishDir "Maktaby.exe"
$installerDir = "$PSScriptRoot\Installer"

# Arch token as it appears in a release asset name (win-x64 -> x64). Computed, never
# typed per target, so adding a runtime cannot desync the two places that spell it.
$arch = ($Runtime -replace '^win-', '')

# Inno's architecture identifiers are NOT the same vocabulary as .NET RIDs, and the
# distinction matters: "x64compatible" also matches Windows 11 on ARM running the x64 app
# under emulation, which is exactly what the x64 setup should do. Plain "x64" is deprecated
# in Inno 6.3+ and means "x64 Windows only".
switch ($Runtime) {
    'win-x64' { $innoArchAllowed = 'x64compatible'; $innoArchInstall = 'x64compatible' }
    'win-arm64' { $innoArchAllowed = 'arm64'; $innoArchInstall = 'arm64' }
    default {
        throw "Unsupported runtime '$Runtime'. Published runtimes are win-x64 and win-arm64."
    }
}

function Show-Help
{
    Write-Host @"
========================================
 Maktaby - build script
========================================

 USAGE
   build.ps1 [-Action <action>] [parameters]
   build.ps1 -? | -Help | Help        show this help

ACTIONS
    Publish    Build + create the Inno installer (versioned from git)
    Build      Quick compile (Debug/Release), no installer
    Clean      Remove bin/obj artifacts
    Version    Show current version info (branch / last tag / built exe)
    Tag        Create an annotated version tag:  -Version <x.y.z[-pre]>
    Help       Show this help

    PARAMETERS
    -Configuration <Debug|Release>          target configuration   (default: Release)
    -Runtime       <rid>                    win-x64 | win-arm64    (default: win-x64)
    -Flavor        <lean|full>              lean = needs .NET Desktop Runtime (default)
                                            full = self-contained, needs nothing
    -Framework     <tfm>                    target framework       (default: net10.0-windows10.0.19041.0)
    -Version       <x.y.z[-pre]>            version for -Action Tag

    OUTPUT
    Installer\Maktaby-<version>-<arch>-setup[-full].exe
    e.g.  Maktaby-1.0.33-beta.1-x64-setup.exe / ...-arm64-setup-full.exe

    VERSIONING (MinVer - driven by git tags)
    git tag -a v1.2.0        -m ""1.2.0""      exact release 1.2.0
    git tag -a v1.2.0-beta.1 -m ""beta""       prerelease 1.2.0-beta.1 (e.g. on develop)
    commits after a tag        height-incremented versions (1.2.<height>)

    EXAMPLES
    build.ps1 -Action Build
    build.ps1 -Action Publish
    build.ps1 -Action Publish -Flavor full
    build.ps1 -Action Publish -Runtime win-arm64 -Flavor full
    build.ps1 -Action Tag -Version 1.2.0-beta.1
    build.ps1 -Action Version
"@
}

# No action / explicit help -> print help and stop.
if ($ShowHelp -or $Action -eq '' -or $Action -eq 'Help')
{
    Show-Help
    exit 0
}

# Target, arch and flavor are all derived once, at the top of this script.
function Get-BuiltExeVersion {
    # ProductVersion == InformationalVersion (MinVer): carries prerelease labels (-beta.N) plus a
    # '+<sha>' suffix we strip. 
    if (Test-Path $publishedExe) {
        $pv = (Get-Item $publishedExe).VersionInfo.ProductVersion
        if ($pv) {
            return ($pv -replace '\+.*$', '')
        }
    }

    return $null
}

function Show-VersionInfo {
    $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Branch       : $branch" -ForegroundColor Gray
    }

    # Nearest version tag + commits since it (MinVer derives versions from exactly these).
    $desc = & git describe --tags --long 2>$null
    if ($LASTEXITCODE -eq 0 -and $desc) {
        Write-Host "Last tag     : $desc   (<tag>-<height>-g<sha> | exact tag when on it)" -ForegroundColor Cyan
    }
    else {
        Write-Host "Last tag     : (none reachable)" -ForegroundColor Yellow
    }

    if (Test-Path $publishedExe) {
        $v = (Get-Item $publishedExe).VersionInfo
        Write-Host "Published exe: File=$($v.FileVersion)  Product=$($v.ProductVersion)" -ForegroundColor Cyan
    }
}

# Size of a publish tree, in MB. Used to report what the trim actually saved.
function Get-TreeSizeMb([string]$Path) {
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [math]::Round($sum / 1MB, 1)
}

# A RID-specific publish leaves runtimes\<rid>\native\WebView2Loader.dll behind AND drops a
# root-level copy next to the apphost. The root copy is the one the apphost actually loads;
# the runtimes tree is the NuGet fallback payload, and it carries the x86 and arm64 loaders
# as well - dead weight in every build, and actively misleading in an arm64 install (an x64
# DLL sitting in the app folder). Measured: 3 files, ~0.5 MB; correctness, not size.
function Remove-RedundantRuntimeAssets([string]$PublishDir) {
    $runtimes = Join-Path $PublishDir "runtimes"
    if (-not (Test-Path $runtimes)) {
        Write-Host "No runtimes\ folder to trim." -ForegroundColor DarkGray
        return
    }

    # Never delete the only copy of something the app may load. If the root file is missing,
    # the publish shape is not the one this script knows about: leave everything alone.
    if (-not (Test-Path (Join-Path $PublishDir "WebView2Loader.dll"))) {
        Write-Warning "No root WebView2Loader.dll - leaving runtimes\ untouched."
        return
    }

    $before = Get-TreeSizeMb $PublishDir
    Remove-Item -LiteralPath $runtimes -Recurse -Force
    $after = Get-TreeSizeMb $PublishDir
    Write-Host ("Trimmed runtimes\ ({0} MB -> {1} MB publish tree)" -f $before, $after) -ForegroundColor DarkGray
}

# A "full" installer that quietly contains a framework-dependent build is the one mistake
# this script can make without noticing: it still compiles, still installs, and fails on the
# user's machine with "You must install .NET". The publish writes "includedFrameworks" into
# the runtimeconfig for self-contained and "framework" for framework-dependent, so the
# difference is readable instead of guessed at.
function Assert-SelfContained([string]$PublishDir) {
    $runtimeConfig = Join-Path $PublishDir "Maktaby.runtimeconfig.json"
    if (-not (Test-Path $runtimeConfig)) {
        throw "Publish produced no Maktaby.runtimeconfig.json - cannot confirm the '$Flavor' flavor."
    }

    $bundles = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).
        runtimeOptions.PSObject.Properties.Name -contains 'includedFrameworks'

    if ($Flavor -eq 'full' -and -not $bundles) {
        throw "Requested -Flavor full but the publish is framework-dependent (no 'includedFrameworks')."
    }
    if ($Flavor -eq 'lean' -and $bundles) {
        throw "Requested -Flavor lean but the publish is self-contained ('includedFrameworks' present)."
    }

    Write-Host "Flavor confirmed: $Flavor" -ForegroundColor DarkGray
}

# Writes the ISPP include that carries the whole build target into installer.iss.
# An include file rather than /D command-line defines: Windows PowerShell 5.1 and 7 quote
# native arguments differently, which corrupted the /D form depending on the host. An ASCII
# include file is engine-proof.
function Write-BuildTargetInclude([string]$AppVersion) {
    $flavorSuffix = if ($Flavor -eq 'full') { '-full' } else { '' }
    $assetBase = "Maktaby-$AppVersion-$arch-setup$flavorSuffix"

    # Path relative to installer.iss (which lives in the solution root) so the script does
    # not depend on where the repo was cloned.
    $publishRelative = "src\Maktaby\bin\Publish\$Runtime-$Flavor"

    # MyRepoRoot is absolute on purpose: ISPP resolves a relative MessagesFile against
    # SourceDir (the publish folder), not against installer.iss, so a relative path there
    # silently points at a file that does not exist.
    $lines = @(
        '; GENERATED by build.ps1 - do not edit, do not commit (see .gitignore).'
        '; Every value here describes the ONE build target this installer packages:'
        '; the asset name the release workflow expects, the folder it publishes from,'
        '; and the architecture the setup is allowed to install on.'
        ('#define MyAppVersion  "' + $AppVersion + '"')
        ('#define MyRuntime     "' + $Runtime + '"')
        ('#define MyArch        "' + $arch + '"')
        ('#define MyFlavor      "' + $Flavor + '"')
        ('#define MyAssetBase   "' + $assetBase + '"')
        ('#define MyAssetName   "' + $assetBase + '.exe"')
        ('#define MyOutputDir   "' + $installerDir + '"')
        ('#define MyPublishDir  "' + $publishRelative + '"')
        ('#define MyRepoRoot    "' + $solutionRoot + '"')
        ('#define MyInnoArchAllowed "' + $innoArchAllowed + '"')
        ('#define MyInnoArchInstall "' + $innoArchInstall + '"')
    )

    Set-Content -LiteralPath (Join-Path $solutionRoot "BuildTarget.inc") -Value $lines -Encoding Ascii
    return $assetBase + '.exe'
}

# Publishes the project, then compiles the Inno installer. The computed version is forwarded to
# the installer script via BuildTarget.inc so prerelease labels (e.g. 1.1.0-beta.1) appear in
# the name.
function Publish-Project {
    param([string]$Config)

    Write-Host "Publishing in $Config configuration..." -ForegroundColor Yellow
    Write-Host "Target: $Runtime ($arch) | $Flavor | Framework: $Framework" -ForegroundColor Gray

    # Clear stale output first: the installer packs *.* recursively, so leftovers from previous
    # publishes (renamed/removed files, old localized folders) would leak into it.
    if (Test-Path $publishDir) {
        Write-Host "Clearing $publishDir ..." -ForegroundColor Gray
        Remove-Item -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "`nStep 1/4: Publishing application..." -ForegroundColor Cyan

    $publishArgs = @(
        "publish", $project
        "--configuration", $Config
        "--output", $publishDir
        "--runtime", $Runtime
        "--framework", $Framework
        "--self-contained", ($(if ($Flavor -eq 'full') { 'true' } else { 'false' }))
    )

    Write-Host "Running: dotnet $($publishArgs -join ' ')" -ForegroundColor Gray

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publish failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "✓ Application published successfully!" -ForegroundColor Green

    Assert-SelfContained $publishDir
    Remove-RedundantRuntimeAssets $publishDir

    $appVersion = Get-BuiltExeVersion
    if (-not $appVersion) {
        Write-Host "Warning: could not determine app version, installer falls back to exe metadata" -ForegroundColor Yellow
    }
    else {
        Write-Host "App version: $appVersion" -ForegroundColor Green
    }

    Write-Host "`nStep 2/4: Preparing installer..." -ForegroundColor Cyan

    $issPath = Join-Path $solutionRoot "installer.iss"
    if (-not (Test-Path $issPath)) {
        Write-Host "Error: installer.iss not found at $issPath" -ForegroundColor Red
        exit 1
    }

    if (-not (Test-Path $installerDir)) {
        New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
    }

    # Written before iscc runs, and before we claim success, so a failed compile can never
    # leave a stale BuildTarget.inc naming a different target.
    $assetName = Write-BuildTargetInclude $appVersion
    Write-Host "Release asset name: $assetName" -ForegroundColor Cyan
    Write-Host "Installer will be created in: $installerDir" -ForegroundColor Cyan

    Write-Host "`nStep 3/4: Building installer..." -ForegroundColor Cyan

    if (Get-Command iscc -ErrorAction SilentlyContinue) {
        Write-Host "Found ISCC in PATH, building installer..." -ForegroundColor Green

        Write-Host "Running: iscc $issPath  (target via BuildTarget.inc)" -ForegroundColor Gray

        # Plain argument: each host quotes natively when needed. (Manually embedded quotes broke
        # pwsh 7, which escapes them into the argument -> invalid-path error inside ISCC.)
        & iscc $issPath
        if ($LASTEXITCODE -ne 0) {
            Write-Host "`n✗ Installer creation failed with exit code $LASTEXITCODE" -ForegroundColor Red
            exit 1
        }

        # Assert the file ISCC should have written actually exists, by name. "iscc exited 0"
        # is not proof: OutputDir/Silent/etc. can change what it emits, and a release
        # workflow that publishes by globbing "the newest exe" would then publish something
        # from a previous build.
        $expected = Join-Path $installerDir $assetName
        if (-not (Test-Path $expected)) {
            Write-Host "`n✗ iscc reported success but '$expected' does not exist." -ForegroundColor Red
            exit 1
        }

        Write-Host "`nStep 4/4: Verifying installer..." -ForegroundColor Cyan
        Write-Host "✓ Installer created successfully!" -ForegroundColor Green
        Write-Host "Location: $expected" -ForegroundColor Cyan
        Write-Host "Size: $([math]::Round((Get-Item $expected).Length / 1MB, 2)) MB" -ForegroundColor Cyan
    }
    else {
Write-Host "`n⚠ Inno Setup Compiler (iscc) not found in PATH." -ForegroundColor Yellow
        Write-Host "BuildTarget.inc has been written, so you can compile the installer yourself:" -ForegroundColor Yellow
        Write-Host "iscc `"$issPath`"" -ForegroundColor Cyan
        Write-Host "`nThe application has been published successfully to: $publishDir" -ForegroundColor Green
    }
}

# Quick build without publishing.
function Build-Project {
    param([string]$Config)

    Write-Host "Building in $Config configuration..." -ForegroundColor Yellow

    Write-Host "`nRestoring NuGet packages..." -ForegroundColor Gray
    dotnet restore $project
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Restore failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "`nBuilding solution..." -ForegroundColor Gray
    dotnet build $project --configuration $Config --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "`n✓ Build completed successfully!" -ForegroundColor Green

    $outputPath = Join-Path $solutionRoot "src\Maktaby\bin\$Config\$Framework"
    if (Test-Path $outputPath) {
        Write-Host "`nOutput location: $outputPath" -ForegroundColor Cyan
        $exePath = Join-Path $outputPath "Maktaby.exe"
        if (Test-Path $exePath) {
            $v = (Get-Item $exePath).VersionInfo
            Write-Host "Executable Version : $($v.FileVersion)" -ForegroundColor Green
            Write-Host "Product Version    : $($v.ProductVersion)" -ForegroundColor Green
        }
    }
}

function Clean-Project {
    Write-Host "Cleaning build artifacts..." -ForegroundColor Yellow

    dotnet clean $project --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Clean failed!" -ForegroundColor Red
        exit 1
    }

    Get-ChildItem -Path $solutionRoot -Include bin, obj -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch "\\packages\\" } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "✓ Clean completed" -ForegroundColor Green
}

switch ($Action) {
    "Publish" {
        Publish-Project $Configuration
    }
    "Build" {
        Build-Project $Configuration
    }
    "Clean" {
        Clean-Project
    }
    "Version" {
        Show-VersionInfo
    }
    "Tag" {
        if (-not $Version) {
            Write-Host "Error: provide a version, e.g.  build.ps1 -Action Tag -Version 1.2.0-beta.1" -ForegroundColor Red
            exit 1
        }

        $tagName = "v$Version"

        & git rev-parse -q --verify "refs/tags/$tagName" | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Tag $tagName already exists." -ForegroundColor Yellow
            exit 1
        }

        & git tag -a $tagName -m $tagName
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to create tag $tagName" -ForegroundColor Red
            exit 1
        }

        Write-Host "✓ Created tag $tagName" -ForegroundColor Green
        Write-Host "Push it with:  git push origin $tagName" -ForegroundColor Yellow
        Write-Host "Then:          build.ps1 -Action Publish" -ForegroundColor Yellow
    }
}

Write-Host "`n========================================`n" -ForegroundColor Cyan


