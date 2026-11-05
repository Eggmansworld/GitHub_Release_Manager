<#
.SYNOPSIS
  Build, test and package a release, then unpack it into the working folder.

.DESCRIPTION
  1. runs the unit tests
  2. publishes a single-file Eggmans_GitHub_Release_Manager.exe (framework-dependent, needs the .NET 10 runtime)
  3. zips it as artifacts\Eggmans_GitHub_Release_Manager_v<version>_win-x64.zip  (the GitHub release asset)
  4. backs up the working folder's config / repo DB / tracked list into _backup\<timestamp>\
  5. unpacks the zip into the working folder (config, repo DB and tracked_repos.txt are never in the zip,
     so your live data is never overwritten)

.EXAMPLE
  .\publish.ps1
  .\publish.ps1 -NoDeploy
  .\publish.ps1 -Target D:\Tools\EGRM
#>
param(
    [string]$Target = 'C:\Utilities\Eggmans_GitHub_Release_Manager',
    [switch]$NoDeploy,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\EggmansReleaseManager\EggmansReleaseManager.csproj'
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$pubDir = Join-Path $root 'artifacts\publish'
$zip = Join-Path $root "artifacts\Eggmans_GitHub_Release_Manager_v${version}_win-x64.zip"

Write-Host "=== Eggman's GitHub Release Manager v$version ===" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '--- tests' -ForegroundColor Magenta
    dotnet test --project (Join-Path $root 'tests\EggmansReleaseManager.Tests')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not publishing.' }
}

Write-Host '--- publish' -ForegroundColor Magenta
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
dotnet publish $proj -c Release -o $pubDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item (Join-Path $root 'README.md') $pubDir

Write-Host '--- package' -ForegroundColor Magenta
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pubDir '*') -DestinationPath $zip
Write-Host "release zip: $zip"

if ($NoDeploy) { return }

Write-Host "--- deploy -> $Target" -ForegroundColor Magenta
New-Item -ItemType Directory -Force $Target | Out-Null
$live = 'Eggmans_GitHub_Release_Manager_config.json', 'Eggmans_GitHub_Release_Manager_repos.json', 'tracked_repos.txt'
$existing = $live | ForEach-Object { Join-Path $Target $_ } | Where-Object { Test-Path $_ }
if ($existing) {
    $bak = Join-Path $Target ('_backup\' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
    New-Item -ItemType Directory -Force $bak | Out-Null
    Copy-Item $existing $bak
    Write-Host "backed up live data -> $bak"
}

$running = Get-Process -Name 'Eggmans_GitHub_Release_Manager' -ErrorAction SilentlyContinue
if ($running) { throw 'The app is running from the target folder - close it and run again.' }

Expand-Archive -Path $zip -DestinationPath $Target -Force
Write-Host "deployed v$version to $Target" -ForegroundColor Green
