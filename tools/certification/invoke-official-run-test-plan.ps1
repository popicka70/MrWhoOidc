#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConformanceSuitePath,

    [Parameter(Mandatory = $true)]
    [string[]]$RunnerArguments,

    [string]$Alias = "mrwhooidc-local",
    [string]$SuiteHost = "www.certification.openid.net",
    [string]$BaseUrl = "https://localhost:8443",
    [string]$TenantSlug = "default",
    [string]$PublicServerBaseUrl,
    [string]$LocalServerBaseUrl,
    [string]$MtlsServerBaseUrl,
    [string]$OutputDir = ".\tools\certification\.generated",
    [string]$ExportDir,
    [string]$ExpectedFailuresFile,
    [string]$ExpectedSkipsFile,
    [string]$ConformanceToken
)

$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Ensure-TrailingSlash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    return "$(($Url.Trim()).TrimEnd('/'))/"
}

if ([string]::IsNullOrWhiteSpace($PublicServerBaseUrl)) {
    $PublicServerBaseUrl = $BaseUrl
}

if ([string]::IsNullOrWhiteSpace($LocalServerBaseUrl)) {
    $LocalServerBaseUrl = $PublicServerBaseUrl
}

if ([string]::IsNullOrWhiteSpace($MtlsServerBaseUrl)) {
    $MtlsServerBaseUrl = $PublicServerBaseUrl
}

$resolvedOutputDir = Resolve-AbsolutePath -Path $OutputDir
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

$prepareScriptPath = Join-Path $PSScriptRoot "prepare-conformance-suite.ps1"
if (-not (Test-Path -Path $prepareScriptPath)) {
    throw "Missing prepare script: $prepareScriptPath"
}

& $prepareScriptPath `
    -Alias $Alias `
    -SuiteHost $SuiteHost `
    -BaseUrl $BaseUrl `
    -TenantSlug $TenantSlug `
    -PublicServerBaseUrl $PublicServerBaseUrl `
    -LocalServerBaseUrl $LocalServerBaseUrl `
    -MtlsServerBaseUrl $MtlsServerBaseUrl `
    -OutputDir $resolvedOutputDir

$resolvedSuitePath = Resolve-AbsolutePath -Path $ConformanceSuitePath
$runnerScriptPath = Join-Path $resolvedSuitePath "scripts/run-test-plan.py"

if (-not (Test-Path -Path $runnerScriptPath)) {
    throw "Official runner was not found: $runnerScriptPath"
}

if ([string]::IsNullOrWhiteSpace($ExportDir)) {
    $ExportDir = Join-Path $resolvedOutputDir "exports"
}

$resolvedExportDir = Resolve-AbsolutePath -Path $ExportDir
New-Item -ItemType Directory -Path $resolvedExportDir -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($ExpectedFailuresFile)) {
    $ExpectedFailuresFile = Join-Path $resolvedOutputDir "expected-failures.json"
}

if ([string]::IsNullOrWhiteSpace($ExpectedSkipsFile)) {
    $ExpectedSkipsFile = Join-Path $resolvedOutputDir "expected-skips.json"
}

$resolvedExpectedFailuresFile = Resolve-AbsolutePath -Path $ExpectedFailuresFile
$resolvedExpectedSkipsFile = Resolve-AbsolutePath -Path $ExpectedSkipsFile

if (-not (Test-Path -Path $resolvedExpectedFailuresFile)) {
    throw "Expected failures file was not found: $resolvedExpectedFailuresFile"
}

if (-not (Test-Path -Path $resolvedExpectedSkipsFile)) {
    throw "Expected skips file was not found: $resolvedExpectedSkipsFile"
}

$pythonLauncher = Get-Command python -ErrorAction SilentlyContinue
$pythonPrefix = @()

if ($null -eq $pythonLauncher) {
    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($null -eq $pyLauncher) {
        throw "Python was not found. Install Python or use the Windows 'py' launcher."
    }

    $pythonLauncher = $pyLauncher
    $pythonPrefix = @("-3")
}

$env:CONFORMANCE_SERVER = Ensure-TrailingSlash -Url $PublicServerBaseUrl
$env:CONFORMANCE_SERVER_LOCAL = Ensure-TrailingSlash -Url $LocalServerBaseUrl
$env:CONFORMANCE_SERVER_MTLS = Ensure-TrailingSlash -Url $MtlsServerBaseUrl

if (-not [string]::IsNullOrWhiteSpace($ConformanceToken)) {
    $env:CONFORMANCE_TOKEN = $ConformanceToken
}

$commandArgs = @(
    $runnerScriptPath,
    "--export-dir", $resolvedExportDir,
    "--expected-failures-file", $resolvedExpectedFailuresFile,
    "--expected-skips-file", $resolvedExpectedSkipsFile
)
$commandArgs += $RunnerArguments

Write-Host "CONFORMANCE_SERVER=$env:CONFORMANCE_SERVER" -ForegroundColor Cyan
Write-Host "CONFORMANCE_SERVER_LOCAL=$env:CONFORMANCE_SERVER_LOCAL" -ForegroundColor Cyan
Write-Host "CONFORMANCE_SERVER_MTLS=$env:CONFORMANCE_SERVER_MTLS" -ForegroundColor Cyan
Write-Host "Export directory: $resolvedExportDir" -ForegroundColor Cyan
Write-Host "Expected failures file: $resolvedExpectedFailuresFile" -ForegroundColor Cyan
Write-Host "Expected skips file: $resolvedExpectedSkipsFile" -ForegroundColor Cyan
Write-Host "Invoking official runner: $runnerScriptPath" -ForegroundColor Green

Push-Location $resolvedSuitePath
try {
    & $pythonLauncher.Source @pythonPrefix @commandArgs

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}