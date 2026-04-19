#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$Alias = "mrwhooidc-local",
    [string]$SuiteHost = "www.certification.openid.net",
    [string]$BaseUrl = "https://localhost:8443",
    [string]$TenantSlug = "default",
    [string]$DynamicRegistrationInitialAccessToken = "oidf-dcr-initial-access-token",
    [switch]$RequireDynamicRegistration
)

$ErrorActionPreference = "Stop"

$issuer = "$BaseUrl/t/$TenantSlug"
$discoveryUrl = "$issuer/.well-known/openid-configuration"
$healthUrl = "$BaseUrl/health"
$expectedRedirectUri = "https://$SuiteHost/test/a/$Alias/callback"
$authorizeEndpoint = $null
$registrationEndpoint = $null
$parEndpoint = $null

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
        [ValidateSet("GET", "POST", "PUT", "DELETE")]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [string]$Body,

        [string]$ContentType = "application/json",

        [hashtable]$Headers = @{},

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

        if ($Headers.Count -gt 0) {
            $params["Headers"] = $Headers
        }

        if (($Method -eq "POST" -or $Method -eq "PUT") -and $PSBoundParameters.ContainsKey("Body")) {
            $params["Body"] = $Body
            $params["ContentType"] = $ContentType
        }

        return Invoke-WebRequest @params
    }
    catch {
        return $null
    }
}

function Convert-ResponseJson {
    param($Response)

    if ($null -eq $Response -or [string]::IsNullOrWhiteSpace($Response.Content)) {
        return $null
    }

    try {
        return $Response.Content | ConvertFrom-Json -Depth 20
    }
    catch {
        return $null
    }
}

function Get-HeaderValue {
    param(
        $Response,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Response -or $null -eq $Response.Headers) {
        return $null
    }

    try {
        $value = $Response.Headers[$Name]
        if ($null -eq $value) {
            return $null
        }

        if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
            return [string]($value | Select-Object -First 1)
        }

        return [string]$value
    }
    catch {
        return $null
    }
}

function Decode-UriComponent {
    param([string]$Value)

    if ($null -eq $Value) {
        return $null
    }

    return [uri]::UnescapeDataString($Value.Replace('+', ' '))
}

function Get-UriParameter {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [ValidateSet("Query", "Fragment")]
        [string]$Source = "Query"
    )

    if ([string]::IsNullOrWhiteSpace($Uri)) {
        return $null
    }

    $parsedUri = [uri]$Uri
    $raw = if ($Source -eq "Fragment") { $parsedUri.Fragment.TrimStart('#') } else { $parsedUri.Query.TrimStart('?') }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    foreach ($pair in $raw -split '&') {
        if ([string]::IsNullOrWhiteSpace($pair)) {
            continue
        }

        $parts = $pair -split '=', 2
        $key = Decode-UriComponent -Value $parts[0]
        if ($key -ne $Name) {
            continue
        }

        if ($parts.Count -lt 2) {
            return ""
        }

        return Decode-UriComponent -Value $parts[1]
    }

    return $null
}

function New-EncodedParameterString {
    param([hashtable]$Parameters)

    $pairs = foreach ($entry in $Parameters.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            continue
        }

        "{0}={1}" -f [uri]::EscapeDataString([string]$entry.Key), [uri]::EscapeDataString([string]$entry.Value)
    }

    return ($pairs -join '&')
}

function Test-RedirectTargetMatchesExpected {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Location,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedUri
    )

    try {
        $actual = [uri]$Location
        $expected = [uri]$ExpectedUri
        return $actual.Scheme.Equals($expected.Scheme, [StringComparison]::OrdinalIgnoreCase) -and
            $actual.Host.Equals($expected.Host, [StringComparison]::OrdinalIgnoreCase) -and
            $actual.AbsolutePath.Equals($expected.AbsolutePath, [StringComparison]::Ordinal)
    }
    catch {
        return $false
    }
}

function Test-DiscoveryArrayContains {
    param(
        $Discovery,
        [Parameter(Mandatory = $true)]
        [string]$Property,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedValues
    )

    $actualValues = @($Discovery.$Property)
    foreach ($expectedValue in $ExpectedValues) {
        if ($actualValues -contains $expectedValue) {
            Pass "Discovery advertises $Property=$expectedValue"
        }
        else {
            Fail "Discovery does not advertise $Property=$expectedValue"
        }
    }
}

function Invoke-AuthorizeProbe {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Parameters,

        [int]$MaximumRedirection = 0
    )

    $separator = if ($authorizeEndpoint.Contains('?')) { '&' } else { '?' }
    $authorizeUrl = "$authorizeEndpoint$separator$(New-EncodedParameterString -Parameters $Parameters)"
    return Try-Request -Method GET -Uri $authorizeUrl -MaximumRedirection $MaximumRedirection
}

function Test-AuthorizeRedirectError {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [hashtable]$Parameters,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedError,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedState,

        [string]$ExpectedErrorDescriptionContains
    )

    $response = Invoke-AuthorizeProbe -Parameters $Parameters -MaximumRedirection 0
    if ($null -eq $response) {
        Fail "$Name failed because the authorize endpoint did not respond"
        return
    }

    if (-not (@(302, 303) -contains [int]$response.StatusCode)) {
        Fail "$Name expected an external redirect, but authorize returned HTTP $($response.StatusCode)"
        return
    }

    $location = Get-HeaderValue -Response $response -Name "Location"
    if ([string]::IsNullOrWhiteSpace($location)) {
        Fail "$Name returned HTTP $($response.StatusCode) without a Location header"
        return
    }

    if (Test-RedirectTargetMatchesExpected -Location $location -ExpectedUri $expectedRedirectUri) {
        Pass "$Name redirects back to the expected callback URI"
    }
    else {
        Fail "$Name redirected to '$location' instead of the expected callback URI"
        return
    }

    $actualError = Get-UriParameter -Uri $location -Name "error"
    if ($actualError -eq $ExpectedError) {
        Pass "$Name returns error=$ExpectedError"
    }
    else {
        Fail "$Name returned error='$actualError' instead of '$ExpectedError'"
    }

    $actualState = Get-UriParameter -Uri $location -Name "state"
    if ($actualState -eq $ExpectedState) {
        Pass "$Name preserves state=$ExpectedState"
    }
    else {
        Fail "$Name returned state='$actualState' instead of '$ExpectedState'"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedErrorDescriptionContains)) {
        $errorDescription = Get-UriParameter -Uri $location -Name "error_description"
        if (-not [string]::IsNullOrWhiteSpace($errorDescription) -and $errorDescription.Contains($ExpectedErrorDescriptionContains, [StringComparison]::OrdinalIgnoreCase)) {
            Pass "$Name returns an error_description containing '$ExpectedErrorDescriptionContains'"
        }
        else {
            Fail "$Name returned error_description='$errorDescription' which does not contain '$ExpectedErrorDescriptionContains'"
        }
    }
}

function Test-DynamicRegistrationCrud {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Endpoint,

        [Parameter(Mandatory = $true)]
        [string]$InitialAccessToken
    )

    if ([string]::IsNullOrWhiteSpace($InitialAccessToken)) {
        Warn "Skipping DCR CRUD smoke because no dynamic registration initial access token was provided"
        return
    }

    $headers = @{ Authorization = "Bearer $InitialAccessToken" }
    $registerBody = @{
        redirect_uris = @("https://client.example.com/callback")
        client_name = "OIDF Dynamic Smoke Client"
        token_endpoint_auth_method = "client_secret_post"
        grant_types = @("authorization_code", "refresh_token")
        response_types = @("code")
        scope = "openid profile email"
        post_logout_redirect_uris = @("https://client.example.com/post-logout")
    } | ConvertTo-Json -Depth 10

    $registerResponse = Try-Request -Method POST -Uri $Endpoint -Body $registerBody -Headers $headers
    if ($null -eq $registerResponse) {
        Fail "Dynamic client registration did not respond"
        return
    }

    if ([int]$registerResponse.StatusCode -eq 201) {
        Pass "Dynamic client registration creates a client with HTTP 201"
    }
    else {
        Fail "Dynamic client registration returned HTTP $($registerResponse.StatusCode) instead of 201"
        return
    }

    $registration = Convert-ResponseJson -Response $registerResponse
    if ($null -eq $registration) {
        Fail "Dynamic client registration did not return a valid JSON response"
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($registration.client_id)) {
        Pass "Dynamic client registration returns client_id"
    }
    else {
        Fail "Dynamic client registration did not return client_id"
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($registration.client_secret)) {
        Pass "Dynamic client registration returns client_secret"
    }
    else {
        Fail "Dynamic client registration did not return client_secret"
    }

    if (-not [string]::IsNullOrWhiteSpace($registration.registration_access_token)) {
        Pass "Dynamic client registration returns registration_access_token"
    }
    else {
        Fail "Dynamic client registration did not return registration_access_token"
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($registration.registration_client_uri)) {
        Pass "Dynamic client registration returns registration_client_uri"
    }
    else {
        Fail "Dynamic client registration did not return registration_client_uri"
        return
    }

    $expectedRegistrationClientUriPrefix = "$issuer/register/"
    if ($registration.registration_client_uri.StartsWith($expectedRegistrationClientUriPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Pass "registration_client_uri is tenant-aware and points at the issuer-scoped /register endpoint"
    }
    else {
        Fail "registration_client_uri '$($registration.registration_client_uri)' is not issuer-scoped as expected"
    }

    $registrationHeaders = @{ Authorization = "Bearer $($registration.registration_access_token)" }

    $getResponse = Try-Request -Method GET -Uri $registration.registration_client_uri -Headers $registrationHeaders
    if ($null -ne $getResponse -and [int]$getResponse.StatusCode -eq 200) {
        Pass "Dynamic client configuration GET succeeds"
    }
    else {
        $statusCode = if ($null -eq $getResponse) { "no response" } else { $getResponse.StatusCode }
        Fail "Dynamic client configuration GET did not return HTTP 200 (got $statusCode)"
        return
    }

    $getPayload = Convert-ResponseJson -Response $getResponse
    if ($null -ne $getPayload -and $getPayload.client_id -eq $registration.client_id -and $getPayload.client_name -eq "OIDF Dynamic Smoke Client") {
        Pass "Dynamic client configuration GET returns the registered client metadata"
    }
    else {
        Fail "Dynamic client configuration GET did not round-trip the registered metadata"
    }

    $updateBody = @{
        redirect_uris = @("https://client.example.com/callback")
        client_name = "OIDF Dynamic Smoke Client Updated"
        token_endpoint_auth_method = "client_secret_post"
        grant_types = @("authorization_code", "refresh_token")
        response_types = @("code")
        scope = "openid profile email"
        contacts = @("ops@client.example.com")
        default_max_age = 600
        require_auth_time = $true
        post_logout_redirect_uris = @("https://client.example.com/post-logout")
    } | ConvertTo-Json -Depth 10

    $updateResponse = Try-Request -Method PUT -Uri $registration.registration_client_uri -Body $updateBody -Headers $registrationHeaders
    if ($null -ne $updateResponse -and [int]$updateResponse.StatusCode -eq 200) {
        Pass "Dynamic client configuration PUT succeeds"
    }
    else {
        $statusCode = if ($null -eq $updateResponse) { "no response" } else { $updateResponse.StatusCode }
        Fail "Dynamic client configuration PUT did not return HTTP 200 (got $statusCode)"
        return
    }

    $updatePayload = Convert-ResponseJson -Response $updateResponse
    if ($null -ne $updatePayload -and $updatePayload.client_name -eq "OIDF Dynamic Smoke Client Updated") {
        Pass "Dynamic client configuration PUT returns the updated client_name"
    }
    else {
        Fail "Dynamic client configuration PUT did not round-trip the updated client_name"
    }

    $deleteResponse = Try-Request -Method DELETE -Uri $registration.registration_client_uri -Headers $registrationHeaders
    if ($null -ne $deleteResponse -and [int]$deleteResponse.StatusCode -eq 204) {
        Pass "Dynamic client configuration DELETE succeeds"
    }
    else {
        $statusCode = if ($null -eq $deleteResponse) { "no response" } else { $deleteResponse.StatusCode }
        Fail "Dynamic client configuration DELETE did not return HTTP 204 (got $statusCode)"
    }
}

function Test-ParSmoke {
    param([Parameter(Mandatory = $true)][string]$Endpoint)

    $pkceChallenge = [string]::Join('', (1..43 | ForEach-Object { 'a' }))
    $parParameters = @{
        client_id = 'oidf-basic-primary'
        client_secret = 'oidf-basic-primary-dev-secret'
        response_type = 'code'
        scope = 'openid profile'
        redirect_uri = $expectedRedirectUri
        state = 'par-state'
        nonce = 'par-nonce'
        prompt = 'none'
        code_challenge = $pkceChallenge
        code_challenge_method = 'S256'
    }

    $parResponse = Try-Request -Method POST -Uri $Endpoint -Body (New-EncodedParameterString -Parameters $parParameters) -ContentType 'application/x-www-form-urlencoded'
    if ($null -eq $parResponse) {
        Fail "PAR endpoint did not respond"
        return
    }

    if ([int]$parResponse.StatusCode -eq 201) {
        Pass "PAR endpoint accepts a pushed authorization request"
    }
    else {
        Fail "PAR endpoint returned HTTP $($parResponse.StatusCode) instead of 201"
        return
    }

    $parPayload = Convert-ResponseJson -Response $parResponse
    if ($null -ne $parPayload -and -not [string]::IsNullOrWhiteSpace($parPayload.request_uri)) {
        Pass "PAR response returns request_uri"
    }
    else {
        Fail "PAR response did not return request_uri"
        return
    }

    if ($null -ne $parPayload.expires_in -and [int]$parPayload.expires_in -gt 0) {
        Pass "PAR response returns expires_in"
    }
    else {
        Fail "PAR response did not return a positive expires_in"
    }

    Test-AuthorizeRedirectError -Name 'Authorize with PAR request_uri' -Parameters @{ request_uri = $parPayload.request_uri } -ExpectedError 'login_required' -ExpectedState 'par-state'
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

    $discovery = $discoveryResponse.Content | ConvertFrom-Json -Depth 20
    $authorizeEndpoint = $discovery.authorization_endpoint
    $registrationEndpoint = $discovery.registration_endpoint
    $parEndpoint = $discovery.pushed_authorization_request_endpoint

    if ($discovery.issuer -eq $issuer) {
        Pass "Discovery issuer matches $issuer"
    }
    else {
        Fail "Discovery issuer was '$($discovery.issuer)' instead of '$issuer'"
    }

    if (-not [string]::IsNullOrWhiteSpace($authorizeEndpoint)) {
        Pass "Discovery advertises authorization_endpoint"
    }
    else {
        Fail "Discovery does not advertise authorization_endpoint"
    }

    if (-not [string]::IsNullOrWhiteSpace($discovery.token_endpoint)) {
        Pass "Discovery advertises token_endpoint"
    }
    else {
        Fail "Discovery does not advertise token_endpoint"
    }

    if (-not [string]::IsNullOrWhiteSpace($discovery.userinfo_endpoint)) {
        Pass "Discovery advertises userinfo_endpoint"
    }
    else {
        Fail "Discovery does not advertise userinfo_endpoint"
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

    Test-DiscoveryArrayContains -Discovery $discovery -Property 'response_modes_supported' -ExpectedValues @('query', 'fragment', 'form_post', 'query.jwt', 'fragment.jwt', 'form_post.jwt')
    Test-DiscoveryArrayContains -Discovery $discovery -Property 'scopes_supported' -ExpectedValues @('openid', 'profile', 'email')
    Test-DiscoveryArrayContains -Discovery $discovery -Property 'code_challenge_methods_supported' -ExpectedValues @('S256')

    if ($discovery.claims_parameter_supported -eq $true) {
        Pass "Discovery advertises claims_parameter_supported=true"
    }
    else {
        Fail "Discovery does not advertise claims_parameter_supported=true"
    }

    if ($discovery.request_parameter_supported -eq $true) {
        Pass "Discovery advertises request_parameter_supported=true"
    }
    else {
        Fail "Discovery does not advertise request_parameter_supported=true"
    }

    if ($discovery.request_uri_parameter_supported -eq $true) {
        Pass "Discovery advertises request_uri_parameter_supported=true"
    }
    else {
        Fail "Discovery does not advertise request_uri_parameter_supported=true"
    }

    if ($discovery.authorization_response_iss_parameter_supported -eq $true) {
        Pass "Discovery advertises authorization_response_iss_parameter_supported=true"
    }
    else {
        Fail "Discovery does not advertise authorization_response_iss_parameter_supported=true"
    }

    if ($null -ne $discovery.PSObject.Properties['authorization_details_types_supported']) {
        Pass "Discovery advertises authorization_details_types_supported"
    }
    else {
        Fail "Discovery does not advertise authorization_details_types_supported"
    }

    if (-not [string]::IsNullOrWhiteSpace($discovery.jwks_uri)) {
        Pass "Discovery advertises jwks_uri"
        $jwksResponse = Try-Request -Method GET -Uri $discovery.jwks_uri
        if ($null -ne $jwksResponse -and $jwksResponse.StatusCode -eq 200) {
            $jwks = $jwksResponse.Content | ConvertFrom-Json -Depth 10
            if ($null -ne $jwks.keys -and @($jwks.keys).Count -gt 0) {
                Pass "JWKS endpoint returns at least one key"

                $kidCount = @($jwks.keys | Where-Object { -not [string]::IsNullOrWhiteSpace($_.kid) }).Count
                if ($kidCount -gt 0) {
                    Pass "JWKS exposes key identifiers"
                }
                else {
                    Fail "JWKS keys do not expose kid values"
                }
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
    }
    else {
        if ($RequireDynamicRegistration) {
            Fail "Discovery does not advertise registration_endpoint"
        }
        else {
            Warn "Discovery does not advertise registration_endpoint; Dynamic OP remains outside the current verifier"
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

        $checkSessionResponse = Try-Request -Method GET -Uri $discovery.check_session_iframe
        if ($null -ne $checkSessionResponse -and [int]$checkSessionResponse.StatusCode -eq 200) {
            Pass "check_session_iframe is reachable"
        }
        else {
            Fail "check_session_iframe is not reachable"
        }
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

    if (-not [string]::IsNullOrWhiteSpace($parEndpoint)) {
        Pass "Discovery advertises pushed_authorization_request_endpoint"

        if ($null -ne $discovery.PSObject.Properties['require_pushed_authorization_requests']) {
            Pass "Discovery advertises require_pushed_authorization_requests"
        }
        else {
            Fail "Discovery does not advertise require_pushed_authorization_requests"
        }
    }
    else {
        Warn "Discovery does not advertise pushed_authorization_request_endpoint; PAR smoke will be skipped"
    }
}

if (-not [string]::IsNullOrWhiteSpace($authorizeEndpoint)) {
    foreach ($clientId in @('oidf-basic-primary', 'oidf-basic-secondary', 'oidf-basic-client-secret-post')) {
        $authorizeResponse = Invoke-AuthorizeProbe -Parameters @{
            client_id = $clientId
            response_type = 'code'
            scope = 'openid profile'
            redirect_uri = $expectedRedirectUri
            state = 'cert-state'
            nonce = 'cert-nonce'
        } -MaximumRedirection 0

        if ($null -ne $authorizeResponse -and @(200, 302, 303) -contains [int]$authorizeResponse.StatusCode) {
            Pass "Authorize endpoint resolves for $clientId using the certification redirect URI"
        }
        else {
            Fail "Authorize endpoint did not resolve for $clientId"
        }
    }

    Test-AuthorizeRedirectError -Name 'Authorize prompt=none without session' -Parameters @{
        client_id = 'oidf-basic-primary'
        response_type = 'code'
        scope = 'openid profile'
        redirect_uri = $expectedRedirectUri
        state = 'prompt-none-state'
        nonce = 'prompt-none-nonce'
        prompt = 'none'
    } -ExpectedError 'login_required' -ExpectedState 'prompt-none-state'

    Test-AuthorizeRedirectError -Name 'Authorize with malformed claims parameter' -Parameters @{
        client_id = 'oidf-basic-primary'
        response_type = 'code'
        scope = 'openid profile'
        redirect_uri = $expectedRedirectUri
        state = 'claims-state'
        nonce = 'claims-nonce'
        claims = '{not-json'
    } -ExpectedError 'invalid_request' -ExpectedState 'claims-state' -ExpectedErrorDescriptionContains 'claims parameter is not valid JSON'

    Test-AuthorizeRedirectError -Name 'Authorize with malformed authorization_details' -Parameters @{
        client_id = 'oidf-basic-primary'
        response_type = 'code'
        scope = 'openid profile'
        redirect_uri = $expectedRedirectUri
        state = 'rar-state'
        nonce = 'rar-nonce'
        authorization_details = '{not-json'
    } -ExpectedError 'invalid_request' -ExpectedState 'rar-state' -ExpectedErrorDescriptionContains 'authorization_details must be valid JSON'
}
else {
    Fail "Authorize endpoint could not be checked because discovery did not return authorization_endpoint"
}

if (-not [string]::IsNullOrWhiteSpace($parEndpoint)) {
    Test-ParSmoke -Endpoint $parEndpoint
}

if (-not [string]::IsNullOrWhiteSpace($registrationEndpoint)) {
    Test-DynamicRegistrationCrud -Endpoint $registrationEndpoint -InitialAccessToken $DynamicRegistrationInitialAccessToken
}

Write-Host "Summary: $passCount passed, $failCount failed, $warnCount warnings"

if ($failCount -gt 0) {
    exit 1
}

exit 0