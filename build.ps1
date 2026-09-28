<#
.SYNOPSIS
  Tests, publishes and packages EasyBlackout.
.DESCRIPTION
  1. dotnet test
  2. dotnet publish: self-contained, single-file win-x64 exe (+ vendor SDK DLLs beside it) into artifacts\publish
  3. Inno Setup: artifacts\EasyBlackout-Setup-<version>.exe (skipped with a warning if ISCC isn't installed)
.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipTests
#>
param(
    [switch]$SkipTests,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "EasyBlackout $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host "`n== Tests ==" -ForegroundColor Cyan
    dotnet test (Join-Path $root 'EasyBlackout.sln') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host "`n== Publish ==" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'src\EasyBlackout\EasyBlackout.csproj') -c $Configuration -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Get-ChildItem $publishDir | Format-Table Name, @{n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize

Write-Host "== Installer ==" -ForegroundColor Cyan
$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning 'Inno Setup 6 not found; skipping the installer. Install it with: winget install JRSoftware.InnoSetup'
    Write-Host "Portable build: $publishDir" -ForegroundColor Green
    return
}

& $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $root 'installer\EasyBlackout.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Write-Host "`nInstaller: $(Join-Path $artifacts "EasyBlackout-Setup-$version.exe")" -ForegroundColor Green
