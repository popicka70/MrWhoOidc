#!/usr/bin/env pwsh
# Dot-sourced by the certification scripts. Provides random per-environment credentials for the
# seeded OIDF certification clients, browser user and DCR initial access token, instead of fixed
# literals committed to the repository.
#
# Values are generated on first use and stored in .generated/certification-secrets.json next to
# this script (the .generated directory is git-ignored; the file is created owner-only on Unix), so
# start-self-certification.ps1, prepare-conformance-suite.ps1, verify-self-certification.ps1 and
# invoke-official-run-test-plan.ps1 all agree on the same values. Pass -Rotate (or delete the file)
# to generate new ones; re-render and re-apply the seed manifest afterwards.

function New-CertificationRandomValue {
    param([int]$Bytes = 32)

    $buffer = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-CertificationSecretsPath {
    return Join-Path (Join-Path $PSScriptRoot ".generated") "certification-secrets.json"
}

function Write-CertificationSecretsFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -Path $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    $tempPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $options = [System.IO.FileStreamOptions]::new()
    $options.Mode = [System.IO.FileMode]::CreateNew
    $options.Access = [System.IO.FileAccess]::Write
    if (-not $IsWindows) {
        # Owner-only (0600) from creation.
        $options.UnixCreateMode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
    }

    $stream = [System.IO.FileStream]::new($tempPath, $options)
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Content)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $stream.Dispose()
    }

    [System.IO.File]::Move($tempPath, $Path, $true)
}

function Get-CertificationSecrets {
    param([switch]$Rotate)

    $path = Get-CertificationSecretsPath
    if (-not $Rotate -and (Test-Path -Path $path)) {
        return Get-Content -Path $path -Raw | ConvertFrom-Json
    }

    # Password: random core plus fixed character classes so it satisfies the IdP password policy.
    $secrets = [ordered]@{
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        dcrInitialAccessToken = New-CertificationRandomValue
        browserPassword = "Oidf!$(New-CertificationRandomValue -Bytes 18)a9Z"
        clientSecrets = [ordered]@{
            'oidf-basic-primary' = New-CertificationRandomValue
            'oidf-basic-secondary' = New-CertificationRandomValue
            'oidf-basic-client-secret-post' = New-CertificationRandomValue
        }
    }

    Write-CertificationSecretsFile -Path $path -Content ($secrets | ConvertTo-Json -Depth 5)
    Write-Host "Generated certification credentials: $path" -ForegroundColor Yellow
    return Get-Content -Path $path -Raw | ConvertFrom-Json
}
