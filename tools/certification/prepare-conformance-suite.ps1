#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Alias('Alias')]
    [string]$SuiteAlias = "mrwhooidc-local",
    [string]$SuiteHost = "www.certification.openid.net",
    [string]$ConformanceApiBaseUrl,
    [string]$BaseUrl = "https://localhost:8443",
    [string]$TenantSlug = "default",
    [string]$DynamicRegistrationInitialAccessToken = "oidf-dcr-initial-access-token",
    [string]$BrowserUsername = "oidf-cert-user",
    [string]$BrowserPassword = "OidfCertUser123!",
    [string]$PublicServerBaseUrl,
    [string]$LocalServerBaseUrl,
    [string]$MtlsServerBaseUrl,
    [string]$DynamicOpPlanName,
    [string]$RpInitiatedLogoutOpPlanName,
    [string]$SessionManagementOpPlanName,
    [string]$FrontChannelLogoutOpPlanName,
    [string]$BackChannelLogoutOpPlanName,
    [string]$OutputDir = ".\tools\certification\.generated"
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

function Normalize-BaseUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    return $Url.Trim().TrimEnd('/')
}

function Ensure-TrailingSlash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    return "$(Normalize-BaseUrl -Url $Url)/"
}

function Get-OptionalPlanName {
    param(
        [string]$PlanName
    )

    if ([string]::IsNullOrWhiteSpace($PlanName)) {
        return $null
    }

    return $PlanName.Trim()
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

if ([string]::IsNullOrWhiteSpace($ConformanceApiBaseUrl)) {
    if ($SuiteHost -match '^https?://') {
        $ConformanceApiBaseUrl = $SuiteHost
    }
    else {
        $ConformanceApiBaseUrl = "https://$SuiteHost"
    }
}

$normalizedConformanceApiBaseUrl = Normalize-BaseUrl -Url $ConformanceApiBaseUrl
$normalizedBaseUrl = Normalize-BaseUrl -Url $BaseUrl
$normalizedPublicServerBaseUrl = Normalize-BaseUrl -Url $PublicServerBaseUrl
$normalizedLocalServerBaseUrl = Normalize-BaseUrl -Url $LocalServerBaseUrl
$normalizedMtlsServerBaseUrl = Normalize-BaseUrl -Url $MtlsServerBaseUrl
$issuer = "$normalizedBaseUrl/t/$TenantSlug"
$discoveryUrl = "$issuer/.well-known/openid-configuration"
$jwksUrl = "$issuer/jwks"
$authorizeUrl = "$issuer/authorize"
$registrationUrl = "$issuer/register"
$endSessionUrl = "$issuer/connect/endsession"
$checkSessionUrl = "$issuer/connect/checksession"
$publicIssuer = "$normalizedPublicServerBaseUrl/t/$TenantSlug"
$publicDiscoveryUrl = "$publicIssuer/.well-known/openid-configuration"
$localIssuer = "$normalizedLocalServerBaseUrl/t/$TenantSlug"
$localDiscoveryUrl = "$localIssuer/.well-known/openid-configuration"
$mtlsIssuer = "$normalizedMtlsServerBaseUrl/t/$TenantSlug"
$mtlsDiscoveryUrl = "$mtlsIssuer/.well-known/openid-configuration"

$callbackUrl = "https://$SuiteHost/test/a/$SuiteAlias/callback"
$postLogoutRedirectUrl = "https://$SuiteHost/test/a/$SuiteAlias/post_logout_redirect"
$frontChannelLogoutUrl = "https://$SuiteHost/test/a/$SuiteAlias/frontchannel_logout"
$backChannelLogoutUrl = "https://$SuiteHost/test/a/$SuiteAlias/backchannel_logout"

$resolvedOutputDir = Resolve-AbsolutePath -Path $OutputDir
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

$jsonPath = Join-Path $resolvedOutputDir "conformance-suite-inputs.json"
$notesPath = Join-Path $resolvedOutputDir "conformance-suite-notes.md"
$envPath = Join-Path $resolvedOutputDir "conformance-suite-env.ps1"
$expectedFailuresPath = Join-Path $resolvedOutputDir "expected-failures.json"
$expectedSkipsPath = Join-Path $resolvedOutputDir "expected-skips.json"
$staticRunnerConfigPath = Join-Path $resolvedOutputDir "official-runner-static-op-config.json"
$dynamicRunnerConfigPath = Join-Path $resolvedOutputDir "official-runner-dynamic-op-config.json"

$certificationPlans = [ordered]@{
    configOp = "oidcc-config-certification-test-plan"
    basicOp = "oidcc-basic-certification-test-plan"
    formPostOp = "oidcc-formpost-basic-certification-test-plan"
}

$defaultAdditionalCertificationPlans = [ordered]@{
    dynamicOp = "oidcc-dynamic-certification-test-plan"
    rpInitiatedLogoutOp = "oidcc-rp-initiated-logout-certification-test-plan"
    sessionManagementOp = "oidcc-session-management-certification-test-plan"
    frontChannelLogoutOp = "oidcc-frontchannel-rp-initiated-logout-certification-test-plan"
    backChannelLogoutOp = "oidcc-backchannel-rp-initiated-logout-certification-test-plan"
}

$additionalCertificationPlans = [ordered]@{
    dynamicOp = (Get-OptionalPlanName -PlanName $DynamicOpPlanName) ?? $defaultAdditionalCertificationPlans.dynamicOp
    rpInitiatedLogoutOp = (Get-OptionalPlanName -PlanName $RpInitiatedLogoutOpPlanName) ?? $defaultAdditionalCertificationPlans.rpInitiatedLogoutOp
    sessionManagementOp = (Get-OptionalPlanName -PlanName $SessionManagementOpPlanName) ?? $defaultAdditionalCertificationPlans.sessionManagementOp
    frontChannelLogoutOp = (Get-OptionalPlanName -PlanName $FrontChannelLogoutOpPlanName) ?? $defaultAdditionalCertificationPlans.frontChannelLogoutOp
    backChannelLogoutOp = (Get-OptionalPlanName -PlanName $BackChannelLogoutOpPlanName) ?? $defaultAdditionalCertificationPlans.backChannelLogoutOp
}

$additionalRelevantProfiles = @(
    [ordered]@{
        profile = "Dynamic OP"
        planName = $additionalCertificationPlans.dynamicOp
        runnerConfig = "official-runner-dynamic-op-config.json"
        readiness = "fix-before-run"
        notes = "Relevant for MrWhoOidc because dynamic registration and client configuration endpoints are implemented. Override the default plan label if the hosted suite uses a different identifier."
    },
    [ordered]@{
        profile = "RP-Initiated Logout OP"
        planName = $additionalCertificationPlans.rpInitiatedLogoutOp
        runnerConfig = "official-runner-static-op-config.json"
        readiness = "next"
        notes = "Required for any logout certification submission; uses the seeded post-logout redirect URI and end-session endpoint."
    },
    [ordered]@{
        profile = "Session Management OP"
        planName = $additionalCertificationPlans.sessionManagementOp
        runnerConfig = "official-runner-static-op-config.json"
        readiness = "next"
        notes = "Relevant because the issuer exposes check_session_iframe; pair with RP-Initiated Logout OP for logout certification work."
    },
    [ordered]@{
        profile = "Front-Channel Logout OP"
        planName = $additionalCertificationPlans.frontChannelLogoutOp
        runnerConfig = "official-runner-static-op-config.json"
        readiness = "next"
        notes = "Relevant because the certification manifest seeds front-channel logout URIs for all fallback clients."
    },
    [ordered]@{
        profile = "Back-Channel Logout OP"
        planName = $additionalCertificationPlans.backChannelLogoutOp
        runnerConfig = "official-runner-static-op-config.json"
        readiness = "next"
        notes = "Relevant because the certification manifest seeds back-channel logout URIs for all fallback clients."
    }
)

$browserAutomation = @(
    [ordered]@{
        match = "*/authorize*"
        tasks = @(
            [ordered]@{
                task = "Capture authorize page"
                optional = $true
                match = "*/authorize*"
                commands = @(,
                    @("wait", "xpath", "//*", 10, ".*", "update-image-placeholder-optional")
                )
            },
            [ordered]@{
                task = "Choose local login"
                optional = $true
                match = "*/auth/providers/select*"
                commands = @(,
                    @("click", "id", "btn-local-login")
                )
            },
            [ordered]@{
                task = "Capture login page"
                optional = $true
                match = "*/login*"
                commands = @(,
                    @("wait", "xpath", "//*", 10, ".*", "update-image-placeholder-optional")
                )
            },
            [ordered]@{
                task = "Login"
                optional = $true
                match = "*/login*"
                commands = @(
                    @("text", "name", "Username", $BrowserUsername, "optional"),
                    @("text", "name", "Password", $BrowserPassword, "optional"),
                    @("click", "css", "button[type='submit']")
                )
            },
            [ordered]@{
                task = "Consent"
                optional = $true
                match = "*/consent*"
                commands = @(,
                    @("click", "css", "button[type='submit']")
                )
            },
            [ordered]@{
                task = "Verify Complete"
                optional = $true
                match = "https://$SuiteHost/test/*/callback*code=*"
                commands = @(,
                    @("wait", "id", "submission_complete", 10)
                )
            },
            [ordered]@{
                task = "Verify Form Post Complete"
                optional = $true
                match = "https://$SuiteHost/test/*/callback*"
                commands = @(,
                    @("wait", "id", "submission_complete", 10)
                )
            },
            [ordered]@{
                task = "Verify Error Complete"
                optional = $true
                match = "https://$SuiteHost/test/*/callback*error=*"
                commands = @(,
                    @("wait", "id", "submission_complete", 10)
                )
            }
        )
    },
    [ordered]@{
        match = "*/endsession*"
        tasks = @(
            [ordered]@{
                # The suite detects RP-initiated logout completion server-side via the GET to
                # post_logout_redirect. Avoid brittle element waits here: the post-logout landing
                # page does not expose a 'submission_complete' marker, and a failing wait would
                # interrupt the test. Use a always-matching capture so an optional screenshot can
                # still be taken without ever timing out.
                task = "Capture Logout Result Page"
                optional = $true
                match = "https://$SuiteHost/test/*/post_logout_redirect*"
                commands = @(,
                    @("wait", "xpath", "//*", 5, ".*", "update-image-placeholder-optional")
                )
            }
        )
    }
)

$staticRunnerConfig = [ordered]@{
    alias = $SuiteAlias
    description = "MrWhoOidc OIDC static-client runner config"
    server = [ordered]@{
        discoveryUrl = $publicDiscoveryUrl
        allow_unexpected_metadata_fields = @(
            "resource_indicators_supported",
            "mrwho_cli_client_id",
            "authorization_response_signing_alg_values_supported",
            "dpop_bound_access_tokens",
            "introspection_token_types_supported"
        )
    }
    client = [ordered]@{
        client_id = "oidf-basic-primary"
        client_secret = "oidf-basic-primary-dev-secret"
        scope = "openid profile email"
        redirect_uri = $callbackUrl
        post_logout_redirect_uri = $postLogoutRedirectUrl
    }
    client2 = [ordered]@{
        client_id = "oidf-basic-secondary"
        client_secret = "oidf-basic-secondary-dev-secret"
        scope = "openid profile email"
        redirect_uri = $callbackUrl
        post_logout_redirect_uri = $postLogoutRedirectUrl
    }
    client3 = [ordered]@{
        client_id = "oidf-basic-client-secret-post"
        client_secret = "oidf-basic-client-secret-post-dev-secret"
        scope = "openid profile email"
        redirect_uri = $callbackUrl
        post_logout_redirect_uri = $postLogoutRedirectUrl
    }
    browser = $browserAutomation
}

$dynamicRunnerConfig = [ordered]@{
    alias = $SuiteAlias
    description = "MrWhoOidc OIDC dynamic-client runner config"
    server = [ordered]@{
        discoveryUrl = $publicDiscoveryUrl
        allow_unexpected_metadata_fields = @(
            "resource_indicators_supported",
            "mrwho_cli_client_id",
            "authorization_response_signing_alg_values_supported",
            "dpop_bound_access_tokens",
            "introspection_token_types_supported"
        )
    }
    client = [ordered]@{
        client_name = "MrWhoOidc Dynamic Primary"
    }
    client2 = [ordered]@{
        client_name = "MrWhoOidc Dynamic Secondary"
    }
    client3 = [ordered]@{
        client_name = "MrWhoOidc Dynamic Client Secret Post"
    }
    browser = $browserAutomation
}

$runnerEnvironment = [ordered]@{
    CONFORMANCE_SERVER = Ensure-TrailingSlash -Url $normalizedConformanceApiBaseUrl
    CONFORMANCE_SERVER_LOCAL = Ensure-TrailingSlash -Url $normalizedConformanceApiBaseUrl
    CONFORMANCE_SERVER_MTLS = Ensure-TrailingSlash -Url $normalizedConformanceApiBaseUrl
}

$inputs = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    alias = $SuiteAlias
    suiteHost = $SuiteHost
    suiteApiBaseUrl = $normalizedConformanceApiBaseUrl
    issuer = [ordered]@{
        baseUrl = $normalizedBaseUrl
        tenantSlug = $TenantSlug
        issuer = $issuer
        discovery = $discoveryUrl
        jwks = $jwksUrl
        authorize = $authorizeUrl
        registration = $registrationUrl
        endSession = $endSessionUrl
        checkSession = $checkSessionUrl
        publicBaseUrl = $normalizedPublicServerBaseUrl
        publicIssuer = $publicIssuer
        publicDiscovery = $publicDiscoveryUrl
        localBaseUrl = $normalizedLocalServerBaseUrl
        localIssuer = $localIssuer
        localDiscovery = $localDiscoveryUrl
        mtlsBaseUrl = $normalizedMtlsServerBaseUrl
        mtlsIssuer = $mtlsIssuer
        mtlsDiscovery = $mtlsDiscoveryUrl
    }
    hostedSuite = [ordered]@{
        callback = $callbackUrl
        postLogoutRedirect = $postLogoutRedirectUrl
        frontChannelLogout = $frontChannelLogoutUrl
        backChannelLogout = $backChannelLogoutUrl
    }
    dynamicRegistration = [ordered]@{
        endpoint = $registrationUrl
        initialAccessToken = $DynamicRegistrationInitialAccessToken
    }
    fallbackClients = @(
        [ordered]@{
            clientId = "oidf-basic-primary"
            clientSecret = "oidf-basic-primary-dev-secret"
            redirectUri = $callbackUrl
            postLogoutRedirectUri = $postLogoutRedirectUrl
            frontChannelLogoutUri = $frontChannelLogoutUrl
            backChannelLogoutUri = $backChannelLogoutUrl
        },
        [ordered]@{
            clientId = "oidf-basic-secondary"
            clientSecret = "oidf-basic-secondary-dev-secret"
            redirectUri = $callbackUrl
            postLogoutRedirectUri = $postLogoutRedirectUrl
            frontChannelLogoutUri = $frontChannelLogoutUrl
            backChannelLogoutUri = $backChannelLogoutUrl
        },
        [ordered]@{
            clientId = "oidf-basic-client-secret-post"
            clientSecret = "oidf-basic-client-secret-post-dev-secret"
            redirectUri = $callbackUrl
            postLogoutRedirectUri = $postLogoutRedirectUrl
            frontChannelLogoutUri = $frontChannelLogoutUrl
            backChannelLogoutUri = $backChannelLogoutUrl
        }
    )
    recommendedProfiles = @(
        "Config OP",
        "Basic OP",
        "Form Post OP"
    )
    additionalRelevantProfiles = $additionalRelevantProfiles
    certificationPlans = $certificationPlans
    additionalCertificationPlans = $additionalCertificationPlans
    runnerEnvironment = $runnerEnvironment
    runnerArtifacts = [ordered]@{
        expectedFailuresFile = $expectedFailuresPath
        expectedSkipsFile = $expectedSkipsPath
        notesFile = $notesPath
        envFile = $envPath
        staticRunnerConfigFile = $staticRunnerConfigPath
        dynamicRunnerConfigFile = $dynamicRunnerConfigPath
    }
    browserAutomation = [ordered]@{
        username = $BrowserUsername
        tasks = @(
            "optional authorization-page placeholder capture via scripted browser snapshot",
            "optional provider-selection click via #btn-local-login",
            "optional login-page placeholder capture via scripted browser snapshot",
            "login via Username and Password form fields",
            "optional consent approval via submit button",
            "optional callback completion wait via #submission_complete",
            "optional post-logout completion wait via #submission_complete"
        )
    }
}

$inputs | ConvertTo-Json -Depth 10 | Set-Content -Path $jsonPath
$staticRunnerConfig | ConvertTo-Json -Depth 20 | Set-Content -Path $staticRunnerConfigPath
$dynamicRunnerConfig | ConvertTo-Json -Depth 20 | Set-Content -Path $dynamicRunnerConfigPath

if (-not (Test-Path -Path $expectedFailuresPath)) {
    Set-Content -Path $expectedFailuresPath -Value "[]"
}

if (-not (Test-Path -Path $expectedSkipsPath)) {
    Set-Content -Path $expectedSkipsPath -Value "[]"
}

$envScript = @"
# Generated by prepare-conformance-suite.ps1

`$env:CONFORMANCE_SERVER = '$($runnerEnvironment.CONFORMANCE_SERVER)'
`$env:CONFORMANCE_SERVER_LOCAL = '$($runnerEnvironment.CONFORMANCE_SERVER_LOCAL)'
`$env:CONFORMANCE_SERVER_MTLS = '$($runnerEnvironment.CONFORMANCE_SERVER_MTLS)'
"@
Set-Content -Path $envPath -Value $envScript

$notes = @"
# Conformance Suite Inputs

            "optional callback completion wait via #submission_complete on suite callback pages",
            "optional post-logout completion wait via #submission_complete on suite logout callback pages"
## Issuer

- Alias: $SuiteAlias
- Issuer: $issuer
- Discovery: $discoveryUrl
- JWKS: $jwksUrl
- Registration endpoint: $registrationUrl
- End-session endpoint: $endSessionUrl
- Check-session iframe: $checkSessionUrl

## Hosted Suite Values

- Callback URI: $callbackUrl
- Post-logout redirect URI: $postLogoutRedirectUrl
- Front-channel logout URI: $frontChannelLogoutUrl
- Back-channel logout URI: $backChannelLogoutUrl

## Dynamic Registration

- Registration endpoint: $registrationUrl
- Initial access token: $DynamicRegistrationInitialAccessToken

## Fallback Clients

- oidf-basic-primary / oidf-basic-primary-dev-secret
- oidf-basic-secondary / oidf-basic-secondary-dev-secret
- oidf-basic-client-secret-post / oidf-basic-client-secret-post-dev-secret

## Conformance Suite API

- Suite host: $SuiteHost
- API base: $normalizedConformanceApiBaseUrl

## Official Runner Environment

- CONFORMANCE_SERVER=$($runnerEnvironment.CONFORMANCE_SERVER)
- CONFORMANCE_SERVER_LOCAL=$($runnerEnvironment.CONFORMANCE_SERVER_LOCAL)
- CONFORMANCE_SERVER_MTLS=$($runnerEnvironment.CONFORMANCE_SERVER_MTLS)

The generated runner config JSON files embed the issuer-under-test discovery URL directly:

- Static runner discovery: $publicDiscoveryUrl
- Dynamic runner discovery: $publicDiscoveryUrl

## Generated Artifacts

- Inputs JSON: $jsonPath
- Environment script: $envPath
- Expected failures file: $expectedFailuresPath
- Expected skips file: $expectedSkipsPath
- Static runner config: $staticRunnerConfigPath
- Dynamic runner config: $dynamicRunnerConfigPath

## Certification Plan Names

- Config OP: $($certificationPlans.configOp)
- Basic OP: $($certificationPlans.basicOp)
- Form Post OP: $($certificationPlans.formPostOp)

## Additional Relevant Profiles

- Dynamic OP: use $dynamicRunnerConfigPath after fixing the remaining DCR contract-fidelity items. Plan label: $(if ($additionalCertificationPlans.dynamicOp) { $additionalCertificationPlans.dynamicOp } else { '<confirm hosted-suite label>' }).
- RP-Initiated Logout OP: use $staticRunnerConfigPath and the seeded post-logout redirect URI. Plan label: $(if ($additionalCertificationPlans.rpInitiatedLogoutOp) { $additionalCertificationPlans.rpInitiatedLogoutOp } else { '<confirm hosted-suite label>' }).
- Session Management OP: use $staticRunnerConfigPath and verify `check_session_iframe` behavior. Plan label: $(if ($additionalCertificationPlans.sessionManagementOp) { $additionalCertificationPlans.sessionManagementOp } else { '<confirm hosted-suite label>' }).
- Front-Channel Logout OP: use $staticRunnerConfigPath with the seeded front-channel logout URI. Plan label: $(if ($additionalCertificationPlans.frontChannelLogoutOp) { $additionalCertificationPlans.frontChannelLogoutOp } else { '<confirm hosted-suite label>' }).
- Back-Channel Logout OP: use $staticRunnerConfigPath with the seeded back-channel logout URI. Plan label: $(if ($additionalCertificationPlans.backChannelLogoutOp) { $additionalCertificationPlans.backChannelLogoutOp } else { '<confirm hosted-suite label>' }).

Logout submission rule: include `RP-Initiated Logout OP` plus at least one of `Session Management OP`, `Front-Channel Logout OP`, or `Back-Channel Logout OP`.

## Browser Automation Defaults

- Username: $BrowserUsername
- Password: $BrowserPassword
- Authorization page: optionally captures authorize-page placeholders before continuing.
- Provider picker: clicks `#btn-local-login` when the provider selection page appears.
- Login form: fills `Username` and `Password`, then submits.
- Consent page: approves via the primary submit button when a consent page appears.

## Wrapper Usage

Example with the repo wrapper around the official run-test-plan.py script:

```powershell
`$runnerArgs = @(
    '--list',
    '$($certificationPlans.basicOp)',
    '$staticRunnerConfigPath'
)

& ./tools/certification/invoke-official-run-test-plan.ps1 `
    -ConformanceSuitePath C:\src\conformance-suite `
    -ConformanceToken '<hosted-suite token>' `
    -Alias $SuiteAlias `
    -RunnerArguments `$runnerArgs
```

For actual execution, replace `--list` with the official runner mode you need and choose either the static or dynamic runner config file generated in this directory.
"@
Set-Content -Path $notesPath -Value $notes

Write-Host "Rendered conformance suite inputs: $jsonPath" -ForegroundColor Green
Write-Host "Rendered runner environment script: $envPath" -ForegroundColor Green
Write-Host "Rendered hosted-suite notes: $notesPath" -ForegroundColor Green
Write-Host "Prepared expected-failures file: $expectedFailuresPath" -ForegroundColor Green
Write-Host "Prepared expected-skips file: $expectedSkipsPath" -ForegroundColor Green
Write-Host "Rendered static runner config: $staticRunnerConfigPath" -ForegroundColor Green
Write-Host "Rendered dynamic runner config: $dynamicRunnerConfigPath" -ForegroundColor Green