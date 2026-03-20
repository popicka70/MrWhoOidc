#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDir = "nupkg",

    [switch]$NoBumpVersion,

    [ValidateSet("patch", "minor", "major")]
    [string]$BumpPart = "patch",

    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "MrWhoOidc.Cli/MrWhoOidc.Cli.csproj"
$outputPath = Join-Path $repoRoot $OutputDir
$nugetConfigPath = Join-Path $repoRoot "NuGet.config"
$packageId = "MrWhoOidc.Cli"
$localSourceName = "MrWhoOidcLocal"

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectFile
    )

    $projectContent = Get-Content -Path $ProjectFile -Raw
    $match = [regex]::Match($projectContent, "<Version>([^<]+)</Version>")

    if (-not $match.Success) {
        throw "Could not find a <Version> element in $ProjectFile"
    }

    return $match.Groups[1].Value
}

function Set-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectFile,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $projectContent = Get-Content -Path $ProjectFile -Raw
    $updatedContent = [regex]::Replace($projectContent, "<Version>[^<]+</Version>", "<Version>$Version</Version>", 1)

    if ($updatedContent -eq $projectContent) {
        throw "Failed to update the <Version> element in $ProjectFile"
    }

    Set-Content -Path $ProjectFile -Value $updatedContent
}

function Get-BumpedVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [ValidateSet("patch", "minor", "major")]
        [string]$Part
    )

    $segments = $Version.Split('.')

    if ($segments.Count -ne 3) {
        throw "Version '$Version' is not in major.minor.patch format"
    }

    $major = [int]$segments[0]
    $minor = [int]$segments[1]
    $patch = [int]$segments[2]

    switch ($Part) {
        "major" {
            $major++
            $minor = 0
            $patch = 0
        }
        "minor" {
            $minor++
            $patch = 0
        }
        "patch" {
            $patch++
        }
    }

    return "$major.$minor.$patch"
}

if (-not (Test-Path -Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The dotnet CLI is required but was not found in PATH"
}

$version = Get-ProjectVersion -ProjectFile $projectPath

$bumpVersion = -not $NoBumpVersion

if ($bumpVersion) {
    $newVersion = Get-BumpedVersion -Version $version -Part $BumpPart
    Set-ProjectVersion -ProjectFile $projectPath -Version $newVersion
    $version = $newVersion
    Write-Host "Updated package version to $version" -ForegroundColor Green
}
else {
    Write-Host "Using package version $version" -ForegroundColor Cyan
}

if (-not (Test-Path -Path $outputPath)) {
    New-Item -ItemType Directory -Path $outputPath | Out-Null
}

Write-Host "Packing $packageId ($version)..." -ForegroundColor Cyan
dotnet pack $projectPath -c $Configuration -o $outputPath /p:Version=$version

if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed"
}

if (Test-Path -Path $nugetConfigPath) {
    Write-Host "Local NuGet source '$localSourceName' is available via $nugetConfigPath" -ForegroundColor DarkGray
}

if ($SkipInstall) {
    Write-Host "Package created in $outputPath" -ForegroundColor Green
    exit 0
}

$installedTools = dotnet tool list --global

if ($LASTEXITCODE -ne 0) {
    throw "Failed to query globally installed dotnet tools"
}

if ($installedTools -match "(?im)^\s*MrWhoOidc\.Cli\s+") {
    Write-Host "Removing existing global tool installation..." -ForegroundColor Yellow
    dotnet tool uninstall --global $packageId

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to uninstall existing global tool"
    }
}

Write-Host "Installing $packageId $version from $outputPath..." -ForegroundColor Cyan
dotnet tool install --global --add-source $outputPath --version $version $packageId

if ($LASTEXITCODE -ne 0) {
    throw "Failed to install global tool"
}

Write-Host "mrwho-cli deployed successfully." -ForegroundColor Green
Write-Host "CLI package source: $outputPath" -ForegroundColor DarkGray