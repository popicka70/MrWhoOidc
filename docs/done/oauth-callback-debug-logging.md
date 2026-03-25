# OAuth Callback Debug Logging

> Historical note: This debugging write-up may contain older callback URLs and port numbers from the environment in use at the time. For the current local development endpoint and example configuration, use [README](../../README.md) and [docs/example-applications-guide.md](../example-applications-guide.md).

## Summary

Added comprehensive debug logging to track OAuth/OIDC external authentication flows, specifically to diagnose redirect URI issues and parameter mismatches.

## Logging Added

### 1. Authorization Request Building (`ExternalOidcRequestBuilder.cs`)

**Location**: Line ~69  
**Log Message**: 
```csharp
_logger.LogInformation("Building authorization request: callback={Callback}, responseType={ResponseType}, clientId={ClientId}", 
    callback, responseType, config.ClientId);
```

**What it shows**:
- The callback URL being constructed (`https://localhost:7208/Auth/External/Callback`)
- Response type (should be `code`)
- Client ID being used

### 2. Authorization Redirect (`ExternalOidcHandler.cs`)

**Location**: Line ~137  
**Log Message**:
```csharp
_logger.LogInformation("Redirecting to authorization endpoint ({Mechanism}). AuthRequest URL: {AuthRequestUrl}", 
    authRequest.Mechanism, authRequest.RedirectUrl);
```

**What it shows**:
- The complete authorization URL being redirected to
- The mechanism (GET, POST, JAR)
- Full query string with all OAuth parameters including `redirect_uri`

### 3. Callback Receipt (`ExternalOidcHandler.cs`)

**Location**: Line ~149  
**Log Message**:
```csharp
_logger.LogInformation("OAuth callback received. Path: {Path}, Query: {Query}", 
    http.Request.Path, http.Request.QueryString.Value);
```

**What it shows**:
- The exact path called (`/Auth/External/Callback`)
- All query parameters received (code, state, error, etc.)

### 4. Token Exchange Preparation (`ExternalOidcHandler.cs`)

**Location**: Line ~232  
**Log Message**:
```csharp
_logger.LogInformation("Token exchange: code={CodePreview}, tokenEndpoint={TokenEndpoint}, redirectUri={RedirectUri}, clientId={ClientId}", 
    code.Length > 10 ? code.Substring(0, 10) + "..." : code, 
    discovery.Response!.TokenEndpoint, 
    redirectUri, 
    cfg.ClientId);
```

**What it shows**:
- Authorization code preview (first 10 chars)
- Token endpoint URL
- The redirect_uri being sent in token exchange
- Client ID

### 5. Token Exchange Request (`ExternalOidcTokenExchangeService.cs`)

**Location**: Line ~89  
**Log Message**:
```csharp
_logger.LogInformation("Token exchange POST to {TokenEndpoint}: grant_type=authorization_code, redirect_uri={RedirectUri}, client_id={ClientId}, code={CodePreview}", 
    tokenEndpoint, 
    redirectUri, 
    clientId, 
    code.Length > 10 ? code.Substring(0, 10) + "..." : code);
```

**What it shows**:
- Token endpoint being called
- Exact `redirect_uri` parameter value in POST body
- Client ID
- Authorization code preview

### 6. Token Exchange Failure (`ExternalOidcTokenExchangeService.cs`)

**Location**: Line ~109  
**Log Message**:
```csharp
_logger.LogWarning("Token exchange failed: {Status} {Body}. Sent redirect_uri: {RedirectUri}", 
    (int)tokResp.StatusCode, body, redirectUri);
```

**What it shows**:
- HTTP status code from token endpoint
- Full error response body from authorization server
- The redirect_uri that was sent (to verify if it matches what AS expects)

## Usage

### View Logs in Docker

```powershell
# View last 100 lines with OAuth flow logs
docker logs mrwhooidc-webauth-1 --tail 100

# Follow logs in real-time during login
docker logs mrwhooidc-webauth-1 -f

# Search for specific flow
docker logs mrwhooidc-webauth-1 | Select-String -Pattern "Building authorization request"
```

### Expected Log Flow

For a successful OAuth flow, you should see logs in this sequence:

1. **Start**: `Building authorization request: callback=...`
2. **Redirect**: `Redirecting to authorization endpoint...` (with full URL)
3. **Callback**: `OAuth callback received. Path=/Auth/External/Callback, Query=?code=...&state=...`
4. **Token Prep**: `Token exchange: code=abc123..., tokenEndpoint=..., redirectUri=...`
5. **Token Request**: `Token exchange POST to ...: grant_type=authorization_code, redirect_uri=...`
6. **Success or Failure**: Either successful token validation or error log

### Troubleshooting with Logs

#### Redirect URI Mismatch
Look for:
```
Token exchange failed: 400 {"error":"invalid_grant",...}. Sent redirect_uri: https://localhost:7208/Auth/External/Callback
```
Compare the `redirect_uri` in the log with the configured redirect URIs in the client configuration.

#### Case Sensitivity Issue
The logs will show the exact casing used:
- In authorization request: `redirect_uri=https://localhost:7208/Auth/External/Callback`
- In token exchange: `redirect_uri=https://localhost:7208/Auth/External/Callback`

These must match **exactly** (case-sensitive) with what's configured in the OAuth client.

#### State Parameter Issues
Check the callback log to see if `state` parameter is present and valid.

## Security Notes

- Authorization codes are only logged as 10-character previews (e.g., `abc123...`)
- Client secrets are never logged
- Full tokens are never logged
- Query strings in callback may contain sensitive state data (not PII but security-sensitive)

## Cleanup

Once debugging is complete, consider:
1. Reducing log level from Information to Warning for these specific messages
2. Removing redirect_uri from token exchange logs if no longer needed
3. Keeping callback receipt logging for audit purposes
