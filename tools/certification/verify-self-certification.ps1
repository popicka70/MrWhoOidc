#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$Alias = "mrwhooidc-local",
    [string]$SuiteHost = "www.certification.openid.net",
    [string]$BaseUrl = "https://localhost:8443",
    [string]$TenantSlug = "default",
    [switch]$RequireDynamicRegistration
)

$ErrorActionPreference = "Stop"

$issuer = "$BaseUrl/t/$TenantSlug"
$discoveryUrl = "$issuer/.well-known/openid-configuration"
$healthUrl = "$BaseUrl/health"
$expectedRedirectUri = "https://$SuiteHost/test/a/$Alias/callback"
$authorizeEndpoint = $null
$registrationEndpoint = $null

$passCount = 0
$failCount = 0
$warnCount = 0

function Pass {
    param([string]$Message)
    Write-Host "PASS $Message" -ForegroundColor Green
    $script:passCount++
}

function Fail {
    param([string]$Message)
    Write-Host "FAIL $Message" -ForegroundColor Red
    $script:failCount++
}

function Warn {
    param([string]$Message)
    Write-Host "WARN $Message" -ForegroundColor Yellow
    $script:warnCount++
}

function Try-Request {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("GET", "POST")]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [string]$Body,

        [string]$ContentType = "application/json",

        [int]$MaximumRedirection = 5
    )

    try {
        $params = @{
            Method = $Method
            Uri = $Uri
            SkipCertificateCheck = $true
            TimeoutSec = 15
            SkipHttpErrorCheck = $true
            MaximumRedirection = $MaximumRedirection
        }

        if ($Method -eq "POST") {
            $params["Body"] = $Body
            $params["ContentType"] = $ContentType
        }

        return Invoke-WebRequest @params
    }
    catch {
        return $null
    }
}

$healthResponse = Try-Request -Method GET -Uri $healthUrl
if ($null -ne $healthResponse -and $healthResponse.StatusCode -eq 200) {
    Pass "Health endpoint responds on $healthUrl"
}
else {
    Fail "Health endpoint did not return HTTP 200 on $healthUrl"
}

$discoveryResponse = Try-Request -Method GET -Uri $discoveryUrl
if ($null -eq $discoveryResponse -or $discoveryResponse.StatusCode -ne 200) {
    Fail "Discovery document was not reachable at $discoveryUrl"
}
else {
    Pass "Discovery document is reachable at $discoveryUrl"

    $discovery = $discoveryResponse.Content | ConvertFrom-Json
    $authorizeEndpoint = $discovery.authorization_endpoint
    $registrationEndpoint = $discovery.registration_endpoint

    if ($discovery.issuer -eq $issuer) {
        Pass "Discovery issuer matches $issuer"
    }
    else {
        Fail "Discovery issuer was '$($discovery.issuer)' instead of '$issuer'"
    }

    if (@($discovery.response_types_supported) -contains "code") {
        Pass "Discovery advertises response_type=code"
    }
    else {
        Fail "Discovery does not advertise response_type=code"
    }

    if ((@($discovery.response_types_supported)).Count -eq 1 -and $discovery.response_types_supported[0] -eq "code") {
        Pass "Discovery stays aligned with the current code-only response type contract"
    }
    else {
        Fail "Discovery response_types_supported is not the expected code-only contract"
    }

    if (@($discovery.response_modes_supported) -contains "form_post") {
        Pass "Discovery advertises response_mode=form_post"
    }
    else {
        Fail "Discovery does not advertise response_mode=form_post"
    }

    if (-not [string]::IsNullOrWhiteSpace($discovery.jwks_uri)) {
        Pass "Discovery advertises jwks_uri"
        $jwksResponse = Try-Request -Method GET -Uri $discovery.jwks_uri
        if ($null -ne $jwksResponse -and $jwksResponse.StatusCode -eq 200) {
            $jwks = $jwksResponse.Content | ConvertFrom-Json
            if ($null -ne $jwks.keys -and @($jwks.keys).Count -gt 0) {
                Pass "JWKS endpoint returns at least one key"
            }
            else {
                Fail "JWKS endpoint returned no signing keys"
            }
        }
        else {
            Fail "JWKS endpoint was not reachable"
        }
    }
    else {
        Fail "Discovery does not advertise jwks_uri"
    }

    if (-not [string]::IsNullOrWhiteSpace($registrationEndpoint)) {
        Pass "Discovery advertises registration_endpoint"

        $registrationResponse = Try-Request -Method POST -Uri $registrationEndpoint -Body "{}"
        if ($null -ne $registrationResponse -and $registrationResponse.StatusCode -ne 404) {
            Pass "Registration endpoint is routed and responds with HTTP $($registrationResponse.StatusCode)"
        }
        else {
            Fail "Registration endpoint is not reachable"
        }
    }
    else {
        if ($RequireDynamicRegistration) {
            Fail "Discovery does not advertise registration_endpoint"
        }
        else {
            Warn "Discovery does not advertise registration_endpoint; Dynamic OP remains outside the default first-pass verifier"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($discovery.end_session_endpoint)) {
        Pass "Discovery advertises end_session_endpoint"
    }
    else {
        Fail "Discovery does not advertise end_session_endpoint"
    }

    if (-not [string]::IsNullOrWhiteSpace($discovery.check_session_iframe)) {
        Pass "Discovery advertises check_session_iframe"
    }
    else {
        Fail "Discovery does not advertise check_session_iframe"
    }

    if ($discovery.frontchannel_logout_supported -eq $true) {
        Pass "Discovery advertises frontchannel logout support"
    }
    else {
        Fail "Discovery does not advertise frontchannel logout support"
    }

    if ($discovery.backchannel_logout_supported -eq $true) {
        Pass "Discovery advertises backchannel logout support"
    }
    else {
        Fail "Discovery does not advertise backchannel logout support"
    }
}

if (-not [string]::IsNullOrWhiteSpace($authorizeEndpoint)) {
    foreach ($clientId in @("oidf-basic-primary", "oidf-basic-secondary")) {
        $authorizeUrl = "${authorizeEndpoint}?client_id=$clientId&response_type=code&scope=openid%20profile&redirect_uri=$([uri]::EscapeDataString($expectedRedirectUri))&state=cert-state&nonce=cert-nonce"
        $authorizeResponse = $null

        try {
            $authorizeParams = @{
                Method = "Get"
                Uri = $authorizeUrl
                SkipCertificateCheck = $true
                TimeoutSec = 15
                SkipHttpErrorCheck = $true
            }

            $authorizeResponse = Invoke-WebRequest @authorizeParams
        }
        catch {
            $authorizeResponse = $null
        }

        if ($null -ne $authorizeResponse -and @(200, 302) -contains [int]$authorizeResponse.StatusCode) {
            Pass "Authorize endpoint resolves for $clientId using the certification redirect URI"
        }
        else {
            Fail "Authorize endpoint did not resolve for $clientId"
        }
    }
}
else {
    Fail "Authorize endpoint could not be checked because discovery did not return authorization_endpoint"
}

Write-Host "Summary: $passCount passed, $failCount failed, $warnCount warnings"

if ($failCount -gt 0) {
    exit 1
}

exit 0