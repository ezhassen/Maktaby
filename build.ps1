# Build and Version Management Script for Desktop Boxes
# This script handles automatic versioning and building

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Publish", "Build", "Clean", "Version", "Bump")]
    [string]$Action = "Publish",

    [Parameter(Mandatory = $false)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Major", "Minor", "Build", "Revision")]
    [string]$BumpType = "Build",

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$Runtime = "win-x64",

    [Parameter(Mandatory = $false)]
    [string]$Framework = "net10.0-windows10.0.19041.0",

    [Parameter(Mandatory = $false)]
    [bool]$SelfContained = $false
)

$ErrorActionPreference = "Stop"
$solutionRoot = $PSScriptRoot
$versionFile = Join-Path $solutionRoot "version.json"
$project = "$PSScriptRoot\src\DesktopBoxesUI\DesktopBoxesUI.csproj"
$publishDir = "$PSScriptRoot\src\DesktopBoxesUI\bin\Publish"
$installerDir = "$PSScriptRoot\Installer"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Desktop Boxes - Build Script" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Function to read current version from version.json
function Get-CurrentVersion {
    if (Test-Path $versionFile) {
        $versionData = Get-Content $versionFile | ConvertFrom-Json
        return $versionData.version
    }
    return $null
}

# Function to update version in version.json
function Set-Version {
    param([string]$NewVersion)

    Write-Host "Updating version to: $NewVersion" -ForegroundColor Yellow

    $versionData = Get-Content $versionFile | ConvertFrom-Json
    $versionData.version = $NewVersion

    $versionData | ConvertTo-Json -Depth 10 | Set-Content $versionFile

    Write-Host "✓ Version updated successfully" -ForegroundColor Green
}

# Function to bump version
function Bump-Version {
    param([string]$BumpType)

    $currentVersion = Get-CurrentVersion
    if (-not $currentVersion) {
        Write-Host "Error: Could not read current version" -ForegroundColor Red
        exit 1
    }

    Write-Host "Current version: $currentVersion" -ForegroundColor Cyan

    # Parse version components
    $parts = $currentVersion.Split('.')
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $build = if ($parts.Count -gt 2) { [int]$parts[2] } else { 0 }
    $revision = if ($parts.Count -gt 3) { [int]$parts[3] } else { 0 }

    # Bump the specified component
    switch ($BumpType) {
        "Major" {
            $major++
            $minor = 0
            $build = 0
            $revision = 0
        }
        "Minor" {
            $minor++
            $build = 0
            $revision = 0
        }
        "Build" {
            $build++
            $revision = 0
        }
        "Revision" {
            $revision++
        }
    }

    $newVersion = "$major.$minor.$build.$revision"
    Set-Version $newVersion

    Write-Host "New version: $newVersion" -ForegroundColor Green
}

# Function to display current version info
function Show-VersionInfo {
    $currentVersion = Get-CurrentVersion
    if ($currentVersion) {
        Write-Host "Current Version: $currentVersion" -ForegroundColor Green
        Write-Host "Version File: $versionFile" -ForegroundColor Gray

        # Try to get NB.GV calculated version if available
        try {
            $nbgv = dotnet nbgv --format json 2>$null
            if ($LASTEXITCODE -eq 0) {
                $nbgvData = $nbgv | ConvertFrom-Json
                Write-Host "`nCalculated Version: $($nbgvData.AssemblyVersion)" -ForegroundColor Cyan
                Write-Host "NuGet Version: $($nbgvData.NuGetPackageVersion)" -ForegroundColor Cyan
            }
        }
        catch {
            Write-Host "`nNote: Install NB.GV CLI tool for more details:" -ForegroundColor Yellow
            Write-Host "  dotnet tool install -g nbgv" -ForegroundColor Gray
        }
    }
    else {
        Write-Host "Error: version.json not found" -ForegroundColor Red
    }
}

# Function to publish the project (build + create installer)
function Publish-Project {
    param([string]$Config)

    Write-Host "Publishing in $Config configuration..." -ForegroundColor Yellow
    Write-Host "Runtime: $Runtime | Framework: $Framework | Self-Contained: $SelfContained" -ForegroundColor Gray

    # Step 1: Publish the project using dotnet publish
    Write-Host "`nStep 1/3: Publishing application..." -ForegroundColor Cyan

    $publishArgs = @(
        "publish", $project
        "--configuration", $Config
        "--output", $publishDir
        "--runtime", $Runtime
        "--framework", $Framework
        # "--no-restore"
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
    Write-Host "Output: $publishDir" -ForegroundColor Cyan

    # Get version info for installer
    $currentVersion = Get-CurrentVersion
    if (-not $currentVersion) {
        Write-Host "Warning: Cannot determine version from version.json, using default" -ForegroundColor Yellow
        $currentVersion = "1.0.0"
    }

    # Step 2: Prepare installer
    Write-Host "`nStep 2/3: Preparing installer..." -ForegroundColor Cyan

    $issPath = Join-Path $solutionRoot "installer.iss"
    if (-not (Test-Path $issPath)) {
        Write-Host "Error: installer.iss not found at $issPath" -ForegroundColor Red
        exit 1
    }

    # Create installer output directory
    if (-not (Test-Path $installerDir)) {
        New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
    }

    Write-Host "Installer will be created in: $installerDir" -ForegroundColor Cyan

    # Step 3: Build installer using Inno Setup
    Write-Host "`nStep 3/3: Building installer..." -ForegroundColor Cyan

    if (Get-Command iscc -ErrorAction SilentlyContinue) {
        Write-Host "Found ISCC in PATH, building installer..." -ForegroundColor Green

        Write-Host "Running: iscc $issPath" -ForegroundColor Gray

        &  iscc "$issPath"
        if ($LASTEXITCODE -eq 0) {
            Write-Host "`n✓ Installer created successfully!" -ForegroundColor Green

            # Show installer location
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
        Write-Host "Please run iscc manually:" -ForegroundColor Yellow
        Write-Host "iscc /O$installerDir /F`"Desktop Boxes v$currentVersion`" $issPath" -ForegroundColor Cyan
        Write-Host "`nThe application has been published successfully to: $publishDir" -ForegroundColor Green
    }
}

# Function to build the project (quick build without publishing)
function Build-Project {
    param([string]$Config)

    Write-Host "Building in $Config configuration..." -ForegroundColor Yellow

    # Restore packages
    Write-Host "`nRestoring NuGet packages..." -ForegroundColor Gray
    dotnet restore $project
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Restore failed!" -ForegroundColor Red
        exit 1
    }

    # Build solution
    Write-Host "`nBuilding solution..." -ForegroundColor Gray
    dotnet build $project --configuration $Config --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "`n✓ Build completed successfully!" -ForegroundColor Green

    # Show output paths
    $outputPath = Join-Path $solutionRoot "src\DesktopBoxesUI\bin\$Config\$Framework"
    if (Test-Path $outputPath) {
        Write-Host "`nOutput location: $outputPath" -ForegroundColor Cyan
        $exePath = Join-Path $outputPath "DesktopBoxesUI.exe"
        if (Test-Path $exePath) {
            $fileVersion = (Get-Item $exePath).VersionInfo.FileVersion
            $productVersion = (Get-Item $exePath).VersionInfo.ProductVersion
            Write-Host "Executable Version: $fileVersion" -ForegroundColor Green
            Write-Host "Product Version: $productVersion" -ForegroundColor Green
        }
    }
}

# Function to clean build artifacts
function Clean-Project {
    Write-Host "Cleaning build artifacts..." -ForegroundColor Yellow

    dotnet clean $project --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Clean failed!" -ForegroundColor Red
        exit 1
    }

    # Remove bin and obj folders
    Get-ChildItem -Path $solutionRoot -Include bin, obj -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch "\\packages\\" } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "✓ Clean completed" -ForegroundColor Green
}

# Main execution
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
    "Bump" {
        if ($Version) {
            # Set specific version
            Set-Version $Version
        }
        else {
            # Bump version
            Bump-Version $BumpType
        }
        Write-Host "`nRun 'build.ps1 -Action Publish' to build and create installer with new version" -ForegroundColor Yellow
    }
    default {
        Write-Host "Unknown action: $Action" -ForegroundColor Red
        exit 1
    }
}

Write-Host "`n========================================`n" -ForegroundColor Cyan
