# OAuth/OIDC Constants Quick Reference

**Quick lookup guide for using the new constants in MrWhoOidc**

---

## 📦 Import Statements

```csharp
using MrWhoOidc.Auth.Protocols;  // For OAuthConstants, OidcConstants, SecurityConstants
using MrWhoOidc.Auth.Utils;      // For CryptoHelper
using MrWhoOidc.WebAuth.Handlers; // For ErrorResults
```

---

## 🔑 Common OAuth Parameters

```csharp
// Instead of: form["grant_type"]
form[OAuthConstants.Parameters.GrantType]

// Instead of: form["client_id"]
form[OAuthConstants.Parameters.ClientId]

// Instead of: form["redirect_uri"]
form[OAuthConstants.Parameters.RedirectUri]

// Instead of: form["code"]
form[OAuthConstants.Parameters.Code]

// Instead of: form["refresh_token"]
form[OAuthConstants.Parameters.RefreshToken]

// Instead of: form["scope"]
form[OAuthConstants.Parameters.Scope]
```

---

## 🎫 Grant Types

```csharp
// Instead of: "authorization_code"
OAuthConstants.GrantTypes.AuthorizationCode

// Instead of: "refresh_token"
OAuthConstants.GrantTypes.RefreshToken

// Instead of: "client_credentials"
OAuthConstants.GrantTypes.ClientCredentials

// Instead of: "urn:ietf:params:oauth:grant-type:token-exchange"
OAuthConstants.GrantTypes.TokenExchange
```

---

## ❌ Error Codes

```csharp
// Instead of: "invalid_request"
OAuthConstants.ErrorCodes.InvalidRequest

// Instead of: "invalid_grant"
OAuthConstants.ErrorCodes.InvalidGrant

// Instead of: "unauthorized_client"
OAuthConstants.ErrorCodes.UnauthorizedClient

// Instead of: "unsupported_grant_type"
OAuthConstants.ErrorCodes.UnsupportedGrantType

// Instead of: "invalid_token"
OAuthConstants.ErrorCodes.InvalidToken

// Instead of: "invalid_scope"
OAuthConstants.ErrorCodes.InvalidScope
```

---

## 🛡️ Error Responses (Preferred Method)

```csharp
// Instead of: Results.Json(new { error = "invalid_request", error_description = "..." }, statusCode: 400)
return ErrorResults.InvalidRequest("Missing required parameter", correlationId: corr);

// Instead of: Results.Json(new { error = "invalid_grant" }, statusCode: 400)
return ErrorResults.InvalidGrant();

// Instead of: Results.Json(new { error = "unauthorized_client" }, statusCode: 400)
return ErrorResults.UnauthorizedClient();

// Instead of: Results.Json(new { error = "unsupported_grant_type", ... }, statusCode: 400)
return ErrorResults.UnsupportedGrantType();

// Instead of: Results.Json(new { error = "access_denied" }, statusCode: 403)
return ErrorResults.AccessDenied("User denied consent", state: state);

// With correlation ID for tracing
return ErrorResults.InvalidRequest("Invalid request object", correlationId: correlationId);
```

---

## 🔐 OIDC Scopes

```csharp
// Instead of: "openid"
OidcConstants.Scopes.OpenId

// Instead of: "profile"
OidcConstants.Scopes.Profile

// Instead of: "email"
OidcConstants.Scopes.Email

// Instead of: "offline_access"
OidcConstants.Scopes.OfflineAccess

// Instead of: new[] { "openid", "profile", "email" }
OidcConstants.Scopes.DefaultScopes
```

---

## 🎭 Response Modes

```csharp
// Instead of: "query.jwt"
OidcConstants.ResponseModes.QueryJwt

// Instead of: "form_post.jwt"
OidcConstants.ResponseModes.FormPostJwt

// Instead of: "form_post"
OidcConstants.ResponseModes.FormPost
```

---

## 🏷️ OIDC Claims

```csharp
// Instead of: "sub"
OidcConstants.Claims.Subject

// Instead of: "name"
OidcConstants.Claims.Name

// Instead of: "email"
OidcConstants.Claims.Email

// Instead of: "nonce"
OidcConstants.Claims.Nonce

// Instead of: "at_hash"
OidcConstants.Claims.AtHash

// Instead of: "roles"
OidcConstants.Claims.Roles
```

---

## 🔐 PKCE

```csharp
// Instead of: "S256"
OAuthConstants.CodeChallengeMethods.S256

// Instead of: custom SHA-256 + base64url implementation
var challenge = CryptoHelper.ComputePkceS256(verifier);
```

---

## 🧮 Cryptography Utilities

```csharp
// PKCE S256 challenge
var challenge = CryptoHelper.ComputePkceS256(verifier);

// Token hashing for storage (Base64)
var hash = CryptoHelper.ComputeSha256Base64(token);

// at_hash, c_hash, s_hash (left-most half + base64url)
var atHash = CryptoHelper.ComputeLeftHalfSha256Base64Url(accessToken);

// Hex encoding (for ETags, correlation IDs)
var etag = CryptoHelper.ComputeSha256Hex(content);

// Short hash for bucketing/metrics (first 6 bytes)
var bucket = CryptoHelper.ComputeSha256HexPrefix(clientId, 6);
```

---

## 🔑 JWT Algorithms

```csharp
// Instead of: "RS256"
SecurityConstants.JwtAlgorithms.RS256

// Instead of: "ES256"
SecurityConstants.JwtAlgorithms.ES256

// Instead of: "PS256"
SecurityConstants.JwtAlgorithms.PS256

// Elliptic curves
// Instead of: "P-256"
SecurityConstants.EllipticCurves.P256
```

---

## 🔒 Password Hashing

```csharp
// Instead of: "argon2id"
SecurityConstants.HashAlgorithms.Argon2id

// Example usage
user.HashAlgorithm = SecurityConstants.HashAlgorithms.Argon2id;
user.PasswordHash = passwordHasher.Hash(password);
```

---

## 🌐 Client Assertion Types

```csharp
// Instead of: "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
OAuthConstants.ClientAssertionTypes.JwtBearer

// Example
if (string.Equals(clientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal))
{
    // Validate JWT assertion
}
```

---

## 🎯 Token Types

```csharp
// Instead of: "Bearer"
OAuthConstants.TokenTypes.Bearer

// Instead of: "DPoP"
OAuthConstants.TokenTypes.DPoP

// URN formats
// Instead of: "urn:ietf:params:oauth:token-type:access_token"
OAuthConstants.TokenTypes.AccessToken

// Instead of: "urn:ietf:params:oauth:token-type:jwt"
OAuthConstants.TokenTypes.Jwt
```

---

## 📝 Common Patterns

### Reading Form Parameters
```csharp
var grantType = form[OAuthConstants.Parameters.GrantType].ToString();
var clientId = form[OAuthConstants.Parameters.ClientId].ToString();
var scope = form[OAuthConstants.Parameters.Scope].ToString();
```

### Grant Type Checking
```csharp
if (string.Equals(grantType, OAuthConstants.GrantTypes.ClientCredentials, StringComparison.Ordinal))
{
    // Handle client credentials
}
```

### Scope Validation
```csharp
var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
if (!scopes.Contains(OidcConstants.Scopes.OpenId))
{
    return ErrorResults.InvalidScope("openid scope is required");
}
```

### Error Logging
```csharp
logger.LogWarning("/token {ErrorCode}: missing parameter for client {ClientIdHash}",
    OAuthConstants.ErrorCodes.InvalidRequest,
    Bucketization.Bucket(clientId));
```

---

## 🧪 Testing Examples

### Before
```csharp
var formData = new Dictionary<string, string>
{
    ["grant_type"] = "authorization_code",
    ["code"] = code,
    ["redirect_uri"] = redirectUri,
    ["code_verifier"] = verifier
};
```

### After
```csharp
var formData = new Dictionary<string, string>
{
    [OAuthConstants.Parameters.GrantType] = OAuthConstants.GrantTypes.AuthorizationCode,
    [OAuthConstants.Parameters.Code] = code,
    [OAuthConstants.Parameters.RedirectUri] = redirectUri,
    [OAuthConstants.Parameters.CodeVerifier] = verifier
};
```

---

## 💡 Pro Tips

1. **Use IntelliSense**: Type `OAuthConstants.` and let IntelliSense guide you
2. **Consistent Naming**: All constants follow the same naming pattern
3. **Error Methods**: Prefer `ErrorResults.*` over inline JSON construction
4. **Crypto Helpers**: Use `CryptoHelper` instead of rolling your own SHA-256
5. **Documentation**: Hover over constants to see XML documentation

---

## 🔗 Related Files

- Constants: `MrWhoOidc.Auth/Protocols/*.cs`
- Utilities: `MrWhoOidc.Auth/Utils/CryptoHelper.cs`
- Error Helpers: `MrWhoOidc.WebAuth/Handlers/ErrorResults.cs`
- Proposal: `docs/refactoring-candidates.md`
- Progress: `docs/refactoring-implementation-progress.md`

---

**Last Updated**: October 3, 2025
