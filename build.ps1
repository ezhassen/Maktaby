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

    [Parameter(Mandatory = $false)]
    [bool]$SelfContained = $false,

    [Alias('help')]
    [Parameter(Mandatory = $false)]
    [switch]$ShowHelp
)

$ErrorActionPreference = "Stop"
$solutionRoot = $PSScriptRoot
$project = "$PSScriptRoot\src\Maktaby\Maktaby.csproj"
$publishDir = "$PSScriptRoot\src\Maktaby\bin\Publish"
$publishedExe = Join-Path $publishDir "Maktaby.exe"
$installerDir = "$PSScriptRoot\Installer"

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
   -Runtime       <rid>                    publish RID            (default: win-x64)
   -Framework     <tfm>                    target framework       (default: net10.0-windows10.0.19041.0)
   -SelfContained <true|false>             self-contained publish (default: false)
   -Version       <x.y.z[-pre]>            version for -Action Tag

 VERSIONING (MinVer - driven by git tags)
   git tag -a v1.2.0        -m ""1.2.0""      exact release 1.2.0
   git tag -a v1.2.0-beta.1 -m ""beta""       prerelease 1.2.0-beta.1 (e.g. on develop)
   commits after a tag        height-incremented versions (1.2.<height>)

 EXAMPLES
   build.ps1 -Action Build
   build.ps1 -Action Publish
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

$publishDir = "$PSScriptRoot\src\Maktaby\bin\Publish"
$publishedExe = Join-Path $publishDir "Maktaby.exe"
$installerDir = "$PSScriptRoot\Installer"

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

# Publishes the project, then compiles the Inno installer. The computed version is forwarded to
# the installer script (/DAppVersion) so prerelease labels (e.g. 1.1.0-beta.1) appear in the name.
function Publish-Project {
    param([string]$Config)

    Write-Host "Publishing in $Config configuration..." -ForegroundColor Yellow
    Write-Host "Runtime: $Runtime | Framework: $Framework | Self-Contained: $SelfContained" -ForegroundColor Gray

    # Clear stale output first: the installer packs *.* recursively, so leftovers from previous
    # publishes (renamed/removed files, old localized folders) would leak into it.
    if (Test-Path $publishDir) {
        Write-Host "Clearing $publishDir ..." -ForegroundColor Gray
        Remove-Item -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "`nStep 1/3: Publishing application..." -ForegroundColor Cyan

    $publishArgs = @(
        "publish", $project
        "--configuration", $Config
        "--output", $publishDir
        "--runtime", $Runtime
        "--framework", $Framework
    )

    if ($SelfContained) {
        $publishArgs += "--self-contained", "true"
    }
    else {
        $publishArgs += "--self-contained", "false"
    }

    Write-Host "Running: dotnet $($publishArgs -join ' ')" -ForegroundColor Gray

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publish failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "✓ Application published successfully!" -ForegroundColor Green

    $appVersion = Get-BuiltExeVersion
    if (-not $appVersion) {
        Write-Host "Warning: could not determine app version, installer falls back to exe metadata" -ForegroundColor Yellow
    }
    else {
        Write-Host "App version: $appVersion" -ForegroundColor Green
    }

    Write-Host "`nStep 2/3: Preparing installer..." -ForegroundColor Cyan

    $issPath = Join-Path $solutionRoot "installer.iss"
    if (-not (Test-Path $issPath)) {
        Write-Host "Error: installer.iss not found at $issPath" -ForegroundColor Red
        exit 1
    }

    if (-not (Test-Path $installerDir)) {
        New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
    }

    Write-Host "Installer will be created in: $installerDir" -ForegroundColor Cyan

    Write-Host "`nStep 3/3: Building installer..." -ForegroundColor Cyan

    if (Get-Command iscc -ErrorAction SilentlyContinue) {
        Write-Host "Found ISCC in PATH, building installer..." -ForegroundColor Green

        # Hand the version to ISPP via an include file instead of a /D command-line define:
        # Windows PowerShell 5.1 and 7 quote native arguments differently, which corrupted the
        # /D form depending on the host. An ASCII include file is engine-proof.
        $versionInc = Join-Path $solutionRoot "BuildVersion.inc"
        Set-Content -LiteralPath $versionInc -Value ('#define MyAppVersion "' + $appVersion + '"') -Encoding Ascii

        Write-Host "Running: iscc $issPath  (version via $versionInc)" -ForegroundColor Gray

        # Plain argument: each host quotes natively when needed. (Manually embedded quotes broke
        # pwsh 7, which escapes them into the argument -> invalid-path error inside ISCC.)
        & iscc $issPath
        if ($LASTEXITCODE -eq 0) {
            Write-Host "`n✓ Installer created successfully!" -ForegroundColor Green

            $installerFiles = Get-ChildItem $installerDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($installerFiles) {
                Write-Host "`nInstaller location: $($installerFiles.FullName)" -ForegroundColor Cyan
                Write-Host "Installer size: $([math]::Round($installerFiles.Length / 1MB, 2)) MB" -ForegroundColor Cyan
            }
        }
        else {
            Write-Host "`n✗ Installer creation failed with exit code $LASTEXITCODE" -ForegroundColor Red
            exit 1
        }
    }
    else {
        Write-Host "`n⚠ Inno Setup Compiler (iscc) not found in PATH." -ForegroundColor Yellow
        Write-Host "Publish first (it generates BuildVersion.inc), then run iscc manually:" -ForegroundColor Yellow
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


