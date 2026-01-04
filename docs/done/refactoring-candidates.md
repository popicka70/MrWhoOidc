# Refactoring Candidates for MrWhoOidc.Auth and MrWhoOidc.WebAuth

**Date**: October 3, 2025  
**Focus**: Duplicate implementations, string literals, and code consolidation opportunities

---

## 1. OAuth/OIDC Protocol Constants

### Problem
OAuth and OIDC protocol strings are scattered across multiple files as string literals, making them error-prone and harder to maintain.

### Current Issues
- **Parameter names** (`grant_type`, `client_id`, `redirect_uri`, `response_type`, `code_challenge`, `code_challenge_method`, `scope`, `state`, `nonce`, etc.) are hardcoded everywhere
- **Error codes** (`invalid_request`, `invalid_grant`, `unsupported_grant_type`, `unauthorized_client`, `invalid_token`, `access_denied`, etc.) are duplicated
- **Grant types** (`authorization_code`, `refresh_token`, `client_credentials`, `urn:ietf:params:oauth:grant-type:token-exchange`) repeated
- **Token types** (`urn:ietf:params:oauth:token-type:access_token`, `urn:ietf:params:oauth:token-type:jwt`) hardcoded
- **Scope values** (`openid`, `profile`, `email`, `offline_access`, `roles`) as literals
- **Response types** (`code`) hardcoded
- **Client assertion types** (`urn:ietf:params:oauth:client-assertion-type:jwt-bearer`) repeated

### Recommendation
Create a **`OAuthConstants.cs`** and **`OidcConstants.cs`** in `MrWhoOidc.Auth/Protocols/`:

```csharp
namespace MrWhoOidc.Auth.Protocols;

public static class OAuthConstants
{
    public static class Parameters
    {
        public const string GrantType = "grant_type";
        public const string ClientId = "client_id";
        public const string ClientSecret = "client_secret";
        public const string RedirectUri = "redirect_uri";
        public const string Scope = "scope";
        public const string State = "state";
        public const string Code = "code";
        public const string CodeVerifier = "code_verifier";
        public const string CodeChallenge = "code_challenge";
        public const string CodeChallengeMethod = "code_challenge_method";
        public const string ResponseType = "response_type";
        public const string Nonce = "nonce";
        public const string Resource = "resource";
        public const string Audience = "audience";
        public const string RefreshToken = "refresh_token";
        public const string AccessToken = "access_token";
        public const string IdToken = "id_token";
        public const string TokenType = "token_type";
        public const string ExpiresIn = "expires_in";
        public const string ResponseMode = "response_mode";
        public const string ClientAssertionType = "client_assertion_type";
        public const string ClientAssertion = "client_assertion";
        public const string SubjectToken = "subject_token";
        public const string SubjectTokenType = "subject_token_type";
        public const string RequestedTokenType = "requested_token_type";
        public const string IssuedTokenType = "issued_token_type";
    }

    public static class GrantTypes
    {
        public const string AuthorizationCode = "authorization_code";
        public const string RefreshToken = "refresh_token";
        public const string ClientCredentials = "client_credentials";
        public const string TokenExchange = "urn:ietf:params:oauth:grant-type:token-exchange";
    }

    public static class ResponseTypes
    {
        public const string Code = "code";
    }

    public static class ErrorCodes
    {
        public const string InvalidRequest = "invalid_request";
        public const string InvalidGrant = "invalid_grant";
        public const string InvalidClient = "invalid_client";
        public const string UnauthorizedClient = "unauthorized_client";
        public const string UnsupportedGrantType = "unsupported_grant_type";
        public const string InvalidScope = "invalid_scope";
        public const string InvalidToken = "invalid_token";
        public const string AccessDenied = "access_denied";
        public const string UnsupportedResponseType = "unsupported_response_type";
        public const string ServerError = "server_error";
        public const string TemporarilyUnavailable = "temporarily_unavailable";
        public const string InvalidTarget = "invalid_target";
        public const string UnsupportedResponseMode = "unsupported_response_mode";
        public const string InvalidRequestObject = "invalid_request_object";
        public const string RateLimitExceeded = "rate_limit_exceeded";
        public const string SlowDown = "slow_down";
    }

    public static class TokenTypes
    {
        public const string Bearer = "Bearer";
        public const string DPoP = "DPoP";
        public const string AccessToken = "urn:ietf:params:oauth:token-type:access_token";
        public const string RefreshToken = "urn:ietf:params:oauth:token-type:refresh_token";
        public const string Jwt = "urn:ietf:params:oauth:token-type:jwt";
        public const string IdToken = "urn:ietf:params:oauth:token-type:id_token";
    }

    public static class ClientAssertionTypes
    {
        public const string JwtBearer = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    }

    public static class CodeChallengeMethods
    {
        public const string Plain = "plain";
        public const string S256 = "S256";
    }
}

public static class OidcConstants
{
    public static class Scopes
    {
        public const string OpenId = "openid";
        public const string Profile = "profile";
        public const string Email = "email";
        public const string OfflineAccess = "offline_access";
        public const string Roles = "roles";
    }

    public static class ResponseModes
    {
        public const string Query = "query";
        public const string Fragment = "fragment";
        public const string FormPost = "form_post";
        public const string QueryJwt = "query.jwt";
        public const string FormPostJwt = "form_post.jwt";
    }

    public static class Claims
    {
        public const string Subject = "sub";
        public const string Name = "name";
        public const string Email = "email";
        public const string EmailVerified = "email_verified";
        public const string Picture = "picture";
        public const string Roles = "roles";
        public const string Realm = "realm";
        public const string Nonce = "nonce";
        public const string AuthTime = "auth_time";
        public const string Acr = "acr";
        public const string Amr = "amr";
        public const string Azp = "azp";
        public const string Sid = "sid";
        public const string Idp = "idp";
        public const string AtHash = "at_hash";
        public const string CHash = "c_hash";
    }
}
```

**Files to update** (28+ files):
- `MrWhoOidc.WebAuth/TokenEndpoint/Grants/*.cs` (4 files)
- `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`
- `MrWhoOidc.WebAuth/Handlers/TokenHandler.cs`
- `MrWhoOidc.WebAuth/Handlers/ParHandler.cs`
- `MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs`
- `MrWhoOidc.WebAuth/Handlers/ErrorResults.cs`
- `MrWhoOidc.Auth/Services/AuthorizeService.cs`
- `MrWhoOidc.Auth/Services/TokenService.cs`
- `MrWhoOidc.Auth/Protocols/AuthorizeModels.cs`
- And many more...

---

## 2. Duplicate Issuer Resolution Logic

### Problem
The pattern `options.Issuer ?? $"{http.Request.Scheme}://{http.Request.Host}"` is duplicated in 11+ places.

### Current Locations
- `AuthorizationCodeGrantHandler.cs` line 29
- `ClientCredentialsGrantHandler.cs` line 34
- `RefreshTokenGrantHandler.cs` line 27
- `DiscoveryHandler.cs` line 22
- `AuthorizeHandler.cs` line 549
- `ExternalOidcRequestBuilder.cs` line 65
- `Logout/LogoutExtensions.cs` line 14
- `HttpContextExtensions.cs` line 18
- And more...

### Recommendation
**Consolidate into a single helper** in `MrWhoOidc.WebAuth/Extensions/HttpContextExtensions.cs`:

```csharp
public static class IssuerHelper
{
    public static string GetIssuer(this HttpContext httpContext, OidcOptions options)
    {
        return options.Issuer ?? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    }
    
    public static string GetIssuer(HttpRequest request, string? configuredIssuer)
    {
        return configuredIssuer ?? $"{request.Scheme}://{request.Host}";
    }
}
```

Then replace all 11+ occurrences with the extension method.

---

## 3. SHA-256 Hashing Utilities

### Problem
Multiple implementations of SHA-256 hashing scattered across the codebase:
- `SHA256.Create()` followed by `ComputeHash()` (10+ places)
- `SHA256.HashData()` for one-shot hashing (15+ places)
- Different encoding patterns (UTF8, ASCII)
- Different output formats (Base64, Hex, Base64Url)

### Current Locations
- `TokenService.ComputeS256()` - PKCE verification
- `TokenHashing.Compute()` - token hashing
- `TokenHashing.ComputeLeftHalfBase64Url()` - at_hash/c_hash
- `PublicJwksCache.Sha256Hex()` - ETags
- `CorrelationIdentifiers.ShortHash()` - correlation IDs
- `Bucketization.Bucket()` - client ID bucketing
- `Audit.HashPii()` - PII hashing
- And many more...

### Recommendation
Create a **`CryptoHelper.cs`** in `MrWhoOidc.Auth/Utils/`:

```csharp
namespace MrWhoOidc.Auth.Utils;

public static class CryptoHelper
{
    /// <summary>PKCE S256 code challenge computation (SHA-256 + Base64Url)</summary>
    public static string ComputePkceS256(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(bytes);
    }

    /// <summary>Compute SHA-256 hash and return as Base64 string</summary>
    public static string ComputeSha256Base64(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Compute SHA-256 hash and return as lowercase hex string</summary>
    public static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Compute left-most half of SHA-256 and return base64url (for at_hash/c_hash)</summary>
    public static string ComputeLeftHalfSha256Base64Url(string value)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(value));
        var half = bytes.AsSpan(0, 16);
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(half);
    }

    /// <summary>Compute SHA-256 and return first N bytes as hex (for bucketing/short hashes)</summary>
    public static string ComputeSha256HexPrefix(string value, int byteCount = 6)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..byteCount]);
    }
}
```

**Consolidate** `TokenHashing.cs` into this helper and update all call sites.

---

## 4. Random String/Handle Generation

### Problem
Multiple patterns for generating random strings:
- `CorrelationIdGenerator.GenerateHandle()` - Base64Url encoded
- Various inline uses of `RandomNumberGenerator.Fill()`
- Inconsistent lengths and encodings

### Recommendation
Enhance `CorrelationIdentifiers.cs` or create a dedicated `SecureRandomHelper.cs`:

```csharp
public static class SecureRandomHelper
{
    /// <summary>Generate cryptographically secure random handle (Base64Url, 96 bits)</summary>
    public static string GenerateHandle(int byteLength = 12)
    {
        Span<byte> bytes = stackalloc byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Generate random bytes</summary>
    public static byte[] GenerateRandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
```

---

## 5. Client Authentication Logic

### Problem
Client authentication is duplicated across multiple handlers:
- `TokenHandler.cs` (lines 44-110)
- `ParHandler.cs` (lines 78-125)
- `RevocationHandler.cs` (lines 19-40)
- `Introspection/ClientAuthenticator.cs` (full implementation)

Each implements:
- Basic auth parsing
- Form-based client_id/client_secret
- `private_key_jwt` (client assertion) validation
- mTLS thumbprint checking

### Recommendation
**Extract into a reusable service** `ClientAuthenticationService.cs` in `MrWhoOidc.Auth/Services/`:

```csharp
public interface IClientAuthenticationService
{
    Task<ClientAuthenticationResult> AuthenticateAsync(
        HttpContext http, 
        string? clientId = null,
        bool requireMtls = false,
        CancellationToken ct = default);
}

public sealed record ClientAuthenticationResult(
    bool Success,
    string? ClientId,
    Client? ClientEntity,
    bool UsedPrivateKeyJwt,
    string? ErrorCode = null,
    string? ErrorDescription = null);
```

This would consolidate:
- Basic auth header parsing
- Form credential reading
- Private key JWT validation (via `IClientAssertionValidator`)
- mTLS cert validation
- Client entity lookup

**Update 4+ handlers** to use this service instead of duplicating logic.

---

## 6. Error Response Creation

### Problem
Error responses are created in multiple ways:
- `ErrorResults.InvalidRequest()` / `InvalidGrant()` / etc. (centralized but incomplete)
- Inline `Results.Json(new { error = "...", error_description = "..." }, statusCode: 400)` in 24+ places
- Inconsistent status codes
- Some include `correlation_id`, others don't

### Recommendation
**Enhance `ErrorResults.cs`** to be comprehensive:

```csharp
public sealed class ErrorResults
{
    public static IResult InvalidRequest(string? description = null, string? state = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidRequest, description, state, correlationId, 400);
    
    public static IResult InvalidGrant(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidGrant, description, null, correlationId, 400);
    
    public static IResult UnauthorizedClient(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.UnauthorizedClient, description, null, correlationId, 401);
    
    public static IResult InvalidToken(string? description = null)
        => Create(OAuthConstants.ErrorCodes.InvalidToken, description, null, null, 401);
    
    public static IResult AccessDenied(string? description = null, string? state = null)
        => Create(OAuthConstants.ErrorCodes.AccessDenied, description, state, null, 403);
    
    public static IResult ServerError(string? description = null)
        => Create(OAuthConstants.ErrorCodes.ServerError, description, null, null, 500);
    
    public static IResult UnsupportedGrantType(string? description = null)
        => Create(OAuthConstants.ErrorCodes.UnsupportedGrantType, 
            description ?? "The authorization grant type is not supported by the authorization server.",
            null, null, 400);
    
    public static IResult RateLimitExceeded(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.RateLimitExceeded, description, null, correlationId, 429);
    
    public static IResult InvalidRequestObject(string? description = null, string? correlationId = null)
        => Create(OAuthConstants.ErrorCodes.InvalidRequestObject, description, null, correlationId, 400);

    private static IResult Create(string code, string? description, string? state, string? correlationId, int statusCode)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = code
        };
        if (!string.IsNullOrEmpty(description)) 
            payload["error_description"] = description;
        if (!string.IsNullOrEmpty(state)) 
            payload["state"] = state;
        if (!string.IsNullOrEmpty(correlationId)) 
            payload["correlation_id"] = correlationId;
            
        return Results.Json(payload, statusCode: statusCode);
    }
}
```

**Replace 24+ inline error JSON constructions** with these methods.

---

## 7. Password Hashing Algorithm String

### Problem
The string `"argon2id"` is hardcoded in 4+ places:
- `RegistrationService.cs` line 161
- `Password/Index.cshtml.cs` line 57
- `Admin/Users/Add.cshtml.cs` line 48
- `Admin/Registrations/Index.cshtml.cs` line 95

### Recommendation
Add to constants:

```csharp
public static class SecurityConstants
{
    public static class HashAlgorithms
    {
        public const string Argon2id = "argon2id";
        public const string BCrypt = "bcrypt";
    }
}
```

---

## 8. JWT Algorithm Constants

### Problem
Algorithm names like `"RS256"`, `"ES256"`, `"ES384"`, `"ES512"`, `"P-256"`, `"P-384"`, `"P-521"` are hardcoded as string literals in:
- `Admin/ProviderKeys/Index.cshtml.cs` (10+ occurrences)
- Key generation and management code

### Recommendation
Add to constants:

```csharp
public static class JwtConstants
{
    public static class Algorithms
    {
        public const string RS256 = "RS256";
        public const string RS384 = "RS384";
        public const string RS512 = "RS512";
        public const string ES256 = "ES256";
        public const string ES384 = "ES384";
        public const string ES512 = "ES512";
        public const string PS256 = "PS256";
        public const string PS384 = "PS384";
        public const string PS512 = "PS512";
        public const string HS256 = "HS256";
    }

    public static class Curves
    {
        public const string P256 = "P-256";
        public const string P384 = "P-384";
        public const string P521 = "P-521";
    }

    public static class TokenTypes
    {
        public const string Jwt = "JWT";
        public const string LogoutJwt = "logout+jwt";
    }
}
```

---

## 9. Default Scope Arrays

### Problem
Default scope arrays like `new[] { "openid", "profile", "email" }` are created inline in multiple places:
- `Admin/Providers/Edit.cshtml.cs` line 364
- `External/ExternalOidcRequestBuilder.cs` line 73
- `QrLoginHandler.cs` line 143
- `DiscoveryHandler.cs` line 29

### Recommendation
Add static defaults to `OidcConstants`:

```csharp
public static class OidcConstants
{
    public static class Scopes
    {
        // ... existing constants ...
        
        public static readonly string[] DefaultScopes = { OpenId, Profile, Email };
        public static readonly string[] AllStandardScopes = { OpenId, Profile, Email, OfflineAccess, Roles };
    }
}
```

---

## 10. Token Parameter Extraction Pattern

### Problem
Grant handlers repeat this pattern:
```csharp
var code = context.Form["code"].ToString();
var redirectUri = context.Form["redirect_uri"].ToString();
var codeVerifier = context.Form["code_verifier"].ToString();
```

### Recommendation
Create form parameter helpers:

```csharp
public static class FormExtensions
{
    public static string GetString(this IFormCollection form, string key)
        => form[key].ToString();
    
    public static string? GetStringOrNull(this IFormCollection form, string key)
    {
        var value = form[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    
    public static string[] GetStringArray(this IFormCollection form, string key, char separator = ' ')
    {
        var value = form[key].ToString();
        return string.IsNullOrWhiteSpace(value) 
            ? Array.Empty<string>() 
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
```

---

## Summary Table

| # | Refactoring | Complexity | Impact | Priority | Files Affected |
|---|-------------|------------|--------|----------|----------------|
| 1 | Protocol Constants | Medium | High | **High** | 28+ files |
| 2 | Issuer Resolution | Low | Medium | **High** | 11+ files |
| 3 | SHA-256 Utilities | Medium | Medium | **High** | 25+ files |
| 4 | Random Generation | Low | Low | Medium | 5+ files |
| 5 | Client Auth Service | High | High | **High** | 4+ files |
| 6 | Error Results Enhancement | Medium | High | **High** | 24+ files |
| 7 | Hash Algorithm Constants | Low | Low | Low | 4 files |
| 8 | JWT Algorithm Constants | Low | Medium | Medium | 10+ files |
| 9 | Default Scope Arrays | Low | Low | Low | 4 files |
| 10 | Form Parameter Helpers | Low | Medium | Medium | 4+ files |

---

## Implementation Strategy

### Phase 1: Foundation (High Priority)
1. **Create constants classes** (#1, #7, #8, #9)
   - Create `OAuthConstants.cs`, `OidcConstants.cs`, `JwtConstants.cs`, `SecurityConstants.cs`
   - No breaking changes, just additions

2. **Create utility classes** (#2, #3, #4, #10)
   - Create/enhance `IssuerHelper`, `CryptoHelper`, `SecureRandomHelper`, `FormExtensions`
   - Maintain backward compatibility

3. **Enhance ErrorResults** (#6)
   - Add new methods, keep existing ones

### Phase 2: Migration (Systematic replacement)
4. **Replace string literals** with constants
   - Start with high-traffic paths (token/authorize handlers)
   - Use multi-file search & replace carefully

5. **Replace duplicate issuer logic** with helper
   - Straightforward replacements

6. **Replace SHA-256 calls** with CryptoHelper
   - Consolidate implementations

### Phase 3: Advanced (Complex refactorings)
7. **Extract Client Authentication Service** (#5)
   - Most complex refactoring
   - Requires careful testing
   - Consider implementing incrementally

---

## Testing Recommendations

For each refactoring:
- ✅ Run full test suite: `dotnet test`
- ✅ Manual smoke tests of auth flows
- ✅ Review audit/metrics/observability output
- ✅ Check error response formats (RFC compliance)
- ✅ Verify no behavior changes (unless intended)

---

## Benefits

1. **Maintainability**: Single source of truth for protocol strings
2. **Type Safety**: Reduced risk of typos in protocol values
3. **Discoverability**: IntelliSense helps find available constants
4. **Consistency**: Unified error handling and crypto operations
5. **Testability**: Easier to mock/test extracted services
6. **DRY Principle**: Eliminate 100+ lines of duplicate code

---

## Notes

- All refactorings are **backward compatible** at the protocol level
- No changes to database schema or API contracts
- Can be done incrementally with PRs per section
- Existing tests should continue passing (or require minimal updates)
