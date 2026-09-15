[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "./dist/AIUsageMonitor",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# Obter a versão a partir do AIUsageMonitor.csproj caso não seja passada por parâmetro
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csprojPath = if (Test-Path "$PSScriptRoot/AIUsageMonitor.csproj") { "$PSScriptRoot/AIUsageMonitor.csproj" } else { "./AIUsageMonitor.csproj" }
    if (Test-Path $csprojPath) {
        [xml]$csprojXml = Get-Content $csprojPath
        $Version = $csprojXml.Project.PropertyGroup.Version
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "0.0.0"
    }
}

$versionTag = if ($Version.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) { $Version } else { "v$Version" }
$zipName = "AIUsageMonitor-$versionTag-$Runtime.zip"
$distFolder = "./dist"
$zipPath = Join-Path $distFolder $zipName

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Building AIUsageMonitor $versionTag (Self-Contained) " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# Clean previous build artifacts
if (Test-Path $OutputDir) {
    Write-Host "Cleaning output directory: $OutputDir" -ForegroundColor Gray
    Remove-Item -Recurse -Force $OutputDir
}

if (Test-Path $zipPath) {
    Write-Host "Removing old zip: $zipPath" -ForegroundColor Gray
    Remove-Item -Force $zipPath
}

if (-not (Test-Path $distFolder)) {
    New-Item -ItemType Directory -Path $distFolder | Out-Null
}

Write-Host "Running dotnet publish ($Runtime, Self-Contained, Multi-File)..." -ForegroundColor Yellow

dotnet publish -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "Compressing package into $zipPath..." -ForegroundColor Yellow
Compress-Archive -Path $OutputDir -DestinationPath $zipPath -Force

$zipItem = Get-Item $zipPath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host " Build & Packaging Successful!            " -ForegroundColor Green
Write-Host " Version       : $versionTag" -ForegroundColor Green
Write-Host " Output Folder : $OutputDir" -ForegroundColor Green
Write-Host " Release Zip   : $zipPath ($zipSizeMb MB)" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
