#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$Alias = "mrwhooidc-local",
    [string]$SuiteHost = "www.certification.openid.net",
    [string]$BaseUrl = "https://localhost:8443",
    [string]$TenantSlug = "default",
    [string]$DynamicRegistrationInitialAccessToken = "oidf-dcr-initial-access-token",
    [int]$TimeoutSeconds = 240,
    [switch]$SkipBuild,
    [switch]$RenderOnly,
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")
$generatedDir = Join-Path $scriptDir ".generated"
$templatePath = Join-Path $scriptDir "templates/certification-seed-manifest.template.json"
$manifestPath = Join-Path $generatedDir "certification-seed-manifest.json"
$baseComposePath = Join-Path $repoRoot "docker-compose.dev.yml"
$overlayComposePath = Join-Path $scriptDir "docker-compose.certification.dev.yml"
$verifyScriptPath = Join-Path $scriptDir "verify-self-certification.ps1"

function Get-ComposeTool {
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        try {
            & docker compose version *> $null
            if ($LASTEXITCODE -eq 0) {
                return @{ Command = "docker"; Prefix = @("compose") }
            }
        }
        catch {
        }
    }

    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        return @{ Command = "docker-compose"; Prefix = @() }
    }

    throw "Docker Compose is required but neither 'docker compose' nor 'docker-compose' was found."
}

function Invoke-Discovery {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    return Invoke-RestMethod -Method Get -Uri $Url -SkipCertificateCheck -TimeoutSec 15
}

if (-not (Test-Path -Path $templatePath)) {
    throw "Certification seed template was not found: $templatePath"
}

if (-not (Test-Path -Path $baseComposePath)) {
    throw "Compose file was not found: $baseComposePath"
}

if (-not (Test-Path -Path $overlayComposePath)) {
    throw "Certification overlay was not found: $overlayComposePath"
}

if (-not (Test-Path -Path $generatedDir)) {
    New-Item -ItemType Directory -Path $generatedDir | Out-Null
}

$templateContent = Get-Content -Path $templatePath -Raw
$renderedManifest = $templateContent.Replace("__ALIAS__", $Alias).Replace("__SUITE_HOST__", $SuiteHost).Replace("__DCR_INITIAL_ACCESS_TOKEN__", $DynamicRegistrationInitialAccessToken)

$null = $renderedManifest | ConvertFrom-Json
Set-Content -Path $manifestPath -Value $renderedManifest

Write-Host "Rendered certification manifest: $manifestPath" -ForegroundColor Green
Write-Host "Suite alias: $Alias" -ForegroundColor Cyan
Write-Host "Suite host: $SuiteHost" -ForegroundColor Cyan
Write-Host "Dynamic registration initial access token: $DynamicRegistrationInitialAccessToken" -ForegroundColor DarkGray

if ($RenderOnly) {
    Write-Host "RenderOnly was set; skipping Docker Compose startup." -ForegroundColor Yellow
    exit 0
}

$composeTool = Get-ComposeTool
$composeArgs = @()
$composeArgs += $composeTool.Prefix
$composeArgs += @(
    "-f", $baseComposePath,
    "-f", $overlayComposePath,
    "up", "-d"
)

if (-not $SkipBuild) {
    $composeArgs += "--build"
}

$composeArgs += "webauth"

Push-Location $repoRoot
try {
    Write-Host "Starting WebAuth certification stack..." -ForegroundColor Cyan
    & $composeTool.Command @composeArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose failed to start the certification stack."
    }
}
finally {
    Pop-Location
}

$issuer = "$BaseUrl/t/$TenantSlug"
$discoveryUrl = "$issuer/.well-known/openid-configuration"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$ready = $false

while ((Get-Date) -lt $deadline) {
    try {
        $discovery = Invoke-Discovery -Url $discoveryUrl
        if ($null -ne $discovery -and $discovery.issuer -eq $issuer) {
            $ready = $true
            break
        }
    }
    catch {
    }

    Start-Sleep -Seconds 3
}

if (-not $ready) {
    throw "The certification issuer did not become ready at $discoveryUrl within $TimeoutSeconds seconds."
}

Write-Host "Certification issuer is responding: $issuer" -ForegroundColor Green
Write-Host "Fallback client: oidf-basic-primary / oidf-basic-primary-dev-secret" -ForegroundColor DarkGray
Write-Host "Fallback client: oidf-basic-secondary / oidf-basic-secondary-dev-secret" -ForegroundColor DarkGray
Write-Host "Fallback client: oidf-basic-client-secret-post / oidf-basic-client-secret-post-dev-secret" -ForegroundColor DarkGray

if (-not $SkipVerify) {
    & $verifyScriptPath -Alias $Alias -SuiteHost $SuiteHost -BaseUrl $BaseUrl -TenantSlug $TenantSlug -DynamicRegistrationInitialAccessToken $DynamicRegistrationInitialAccessToken -RequireDynamicRegistration

    if ($LASTEXITCODE -ne 0) {
        throw "Certification verification failed."
    }
}

Write-Host "OpenID self-certification bootstrap is started and ready for Config OP / Basic OP work." -ForegroundColor Green