#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [string]$ExpectedCommit,
    [string]$ExpectedVersion,
    [string]$ExpectedBranch,
    [switch]$UseLocalGitHead
)

$ErrorActionPreference = "Stop"

function Resolve-ExpectedCommit {
    param(
        [string]$CurrentExpectedCommit,
        [switch]$UseGitHead
    )

    if (-not [string]::IsNullOrWhiteSpace($CurrentExpectedCommit)) {
        return $CurrentExpectedCommit.Trim()
    }

    if (-not $UseGitHead) {
        return $null
    }

    try {
        $head = (& git rev-parse HEAD).Trim()
        if ([string]::IsNullOrWhiteSpace($head)) {
            return $null
        }

        return $head
    }
    catch {
        throw "Failed to resolve git HEAD. Run from a git checkout or pass -ExpectedCommit explicitly."
    }
}

$normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')
$resolvedExpectedCommit = Resolve-ExpectedCommit -CurrentExpectedCommit $ExpectedCommit -UseGitHead:$UseLocalGitHead

$response = Invoke-RestMethod -Method Get -Uri "$normalizedBaseUrl/version"

Write-Host "Service: $($response.service)" -ForegroundColor Cyan
Write-Host "Environment: $($response.environment)" -ForegroundColor Cyan
Write-Host "Version: $($response.version)" -ForegroundColor Cyan
Write-Host "InformationalVersion: $($response.informationalVersion)" -ForegroundColor Cyan
Write-Host "Commit: $($response.commit)" -ForegroundColor Cyan

if ($null -ne $response.branch) {
    Write-Host "Branch: $($response.branch)" -ForegroundColor Cyan
}

if ($null -ne $response.repoSlug) {
    Write-Host "RepoSlug: $($response.repoSlug)" -ForegroundColor Cyan
}

if ($null -ne $response.serviceName) {
    Write-Host "ServiceName: $($response.serviceName)" -ForegroundColor Cyan
}

$errors = New-Object System.Collections.Generic.List[string]

if (-not [string]::IsNullOrWhiteSpace($resolvedExpectedCommit) -and $response.commit -ne $resolvedExpectedCommit) {
    $errors.Add("Expected commit '$resolvedExpectedCommit' but deployment reports '$($response.commit)'.")
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $response.version -ne $ExpectedVersion) {
    $errors.Add("Expected version '$ExpectedVersion' but deployment reports '$($response.version)'.")
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedBranch) -and $response.branch -ne $ExpectedBranch) {
    $errors.Add("Expected branch '$ExpectedBranch' but deployment reports '$($response.branch)'.")
}

if ($errors.Count -gt 0) {
    foreach ($error in $errors) {
        Write-Host $error -ForegroundColor Red
    }

    exit 1
}

Write-Host "Deployment version matches the expected values." -ForegroundColor Green