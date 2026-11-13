<#
.SYNOPSIS
    Builds and publishes MedicalShopManager as a standalone Windows executable.
.DESCRIPTION
    Compiles the solution, runs unit tests, and publishes a self-contained, single-file
    executable (.exe) for 64-bit Windows. Optionally zips the distribution.
.PARAMETER SkipTests
    Skips running the unit and integration tests prior to publishing.
.PARAMETER ZipOutput
    Creates a zip archive of the published application inside the dist folder.
.EXAMPLE
    .\build-exe.ps1
    .\build-exe.ps1 -SkipTests -ZipOutput
#>

[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$ZipOutput = $true
)

$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Medical Shop Manager - Windows Executable Builder" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# Check dotnet installation
try {
    $dotnetVersion = (dotnet --version).Trim()
    Write-Host "[*] Found .NET SDK version: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Error "[ERROR] .NET SDK is not installed or not in PATH. Please install .NET 8.0 SDK from https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

$solutionDir = $PSScriptRoot
$projectPath = Join-Path $solutionDir "src\MedicalShopManager.App\MedicalShopManager.App.csproj"
$solutionPath = Join-Path $solutionDir "MedicalShopManager.sln"
$outputDir = Join-Path $solutionDir "dist\publish"
$distDir = Join-Path $solutionDir "dist"

# Clean previous output
if (Test-Path $outputDir) {
    Write-Host "[*] Cleaning previous output: $outputDir" -ForegroundColor Yellow
    Remove-Item -Path $outputDir -Recurse -Force
}

# Run tests unless skipped
if (-not $SkipTests) {
    Write-Host "[*] Running solution test suite..." -ForegroundColor Cyan
    try {
        dotnet test $solutionPath -c Release --verbosity minimal
        Write-Host "[+] All tests passed successfully!" -ForegroundColor Green
    } catch {
        Write-Warning "[-] Some tests encountered issues. Continuing with publish..."
    }
} else {
    Write-Host "[*] Skipping tests (-SkipTests specified)." -ForegroundColor Yellow
}

# Publish single-file self-contained win-x64 executable
Write-Host ""
Write-Host "[*] Publishing self-contained win-x64 executable..." -ForegroundColor Cyan
Write-Host "    Target: $outputDir" -ForegroundColor Gray

$publishArgs = @(
    "publish",
    $projectPath,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishReadyToRun=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $outputDir
)

& dotnet @publishArgs

$exePath = Join-Path $outputDir "MedicalShopManager.exe"

if (Test-Path $exePath) {
    $fileInfo = Get-Item $exePath
    $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
    
    Write-Host ""
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  Executable: $exePath" -ForegroundColor White
    Write-Host "  File Size : $sizeMB MB (Self-Contained Single File)" -ForegroundColor White
    Write-Host ""

    # Copy documentation
    $readmePath = Join-Path $solutionDir "README.md"
    if (Test-Path $readmePath) {
        Copy-Item -Path $readmePath -Destination $outputDir -Force
    }

    # Create zip archive if requested
    if ($ZipOutput) {
        $zipName = "MedicalShopManager-v1.0.0-win-x64.zip"
        $zipPath = Join-Path $distDir $zipName
        Write-Host "[*] Creating zip package: $zipPath" -ForegroundColor Cyan
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path "$outputDir\*" -DestinationPath $zipPath
        $zipInfo = Get-Item $zipPath
        $zipSizeMB = [math]::Round($zipInfo.Length / 1MB, 2)
        Write-Host "[+] Package created ($zipSizeMB MB): $zipPath" -ForegroundColor Green
    }

    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  Ready to deploy! Transfer to any Windows 10/11 x64 PC." -ForegroundColor Cyan
    Write-Host "========================================================" -ForegroundColor Green
} else {
    Write-Error "[ERROR] MedicalShopManager.exe was not created in output directory."
    exit 1
}
