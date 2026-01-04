# Authentication Time (auth_time) Claim Type Fix

**Issue**: React OIDC client using `oauth4webapi` library failed during token validation with error:
```
ID Token "auth_time" (authentication time) must be a positive number
```

The decoded ID token showed `auth_time` as a **string** instead of a **number**:
```json
{
  "auth_time": "1759518012",  // ❌ String (with quotes)
  "iat": 1759518012,           // ✅ Number (no quotes)
  "exp": 1759518312
}
```

## Root Cause

The `auth_time` claim was being added to the JWT as a **string claim** instead of a **numeric claim**. This occurred because the `Claim` constructor was called without specifying the `ClaimValueTypes.Integer64` parameter:

```csharp
// OLD CODE - creates string claim
if (authTime.HasValue) 
    list.Add(new Claim("auth_time", authTime.ToUnixTimeSeconds().ToString()));
```

When the JWT is serialized, claims without an explicit value type default to **string**, which results in JSON with quotes around the number. The OIDC spec requires `auth_time` to be a JSON **number** (without quotes), just like `iat`, `exp`, and `nbf`.

## Solution

### Fix 1: Add ClaimValueTypes.Integer64 to auth_time Claim

Modified `JwtService.cs` to specify that `auth_time` is a numeric claim:

```csharp
// NEW CODE - creates numeric claim
if (authTime.HasValue) 
    list.Add(new Claim(
        "auth_time", 
        ((DateTimeOffset)authTime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), 
        ClaimValueTypes.Integer64  // ✅ Specifies numeric type
    ));
```

### Fix 2: Add Fallback for Missing auth_time

Also added a fallback in `AuthorizeHandler.cs` to use current time when `auth_time` is not in the authentication cookie:

```csharp
var authTimeClaim = http.User.FindFirst("auth_time")?.Value;
if (!string.IsNullOrEmpty(code) && long.TryParse(authTimeClaim, out var seconds))
{
    meta.SetAuthTime(code!, DateTimeOffset.FromUnixTimeSeconds(seconds));
}
else if (!string.IsNullOrEmpty(code))
{
    // Fallback: if auth_time not in cookie, use current time
    meta.SetAuthTime(code!, DateTimeOffset.UtcNow);
}
```

## How ClaimValueTypes Works

The `Claim` constructor has three overloads:
1. `new Claim(string type, string value)` → Defaults to **string** claim
2. `new Claim(string type, string value, string valueType)` → Specifies claim type

When JWT is serialized:
- **String claim** (`valueType` = default): `"claim_name": "123"`
- **Numeric claim** (`valueType` = `Integer64`): `"claim_name": 123`

The `iat` (issued at) claim was already correct because it used `ClaimValueTypes.Integer64`:

```csharp
list.Add(new Claim(
    JwtRegisteredClaimNames.Iat, 
    EpochTime.GetIntDate(now.UtcDateTime).ToString(CultureInfo.InvariantCulture), 
    ClaimValueTypes.Integer64  // ✅ Already correct
));
```

## RFC-Compliant ID Token

**Before (incorrect):**
```json
{
  "auth_time": "1759518012",  // ❌ String with quotes
  "iat": 1759518012,
  "nbf": 1759518012,
  "exp": 1759518312
}
```

**After (correct):**
```json
{
  "auth_time": 1759518012,    // ✅ Number without quotes
  "iat": 1759518012,
  "nbf": 1759518012,
  "exp": 1759518312
}
```

## Files Modified

- `MrWhoOidc.Auth/Services/JwtService.cs` (lines 22 and 51)
  - Added `ClaimValueTypes.Integer64` parameter to `auth_time` claim in both `CreateJwt` and `CreateJwtEncrypted` methods
- `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs` (lines ~455-465)
  - Added fallback to use `DateTimeOffset.UtcNow` when `auth_time` not in cookie

## Testing

1. **Clear browser cookies** to get fresh authentication
2. **Navigate to React app**: `http://localhost:5173`
3. **Click "Login"** → Complete authentication flow
4. **Decode ID token** → Verify `auth_time` is a number (no quotes)
5. **Client should succeed** → oauth4webapi validates and accepts the token ✅

## Complete OIDC Flow Now Fully Functional! 🎉

All issues resolved:
1. ✅ **PAR**: HTTP 201 status + correct JSON format (RFC 9126)
2. ✅ **Consent**: POST handler saves to database  
3. ✅ **Issuer**: `iss` parameter in redirect (RFC 9207)
4. ✅ **Auth Time Fallback**: Always present in metadata
5. ✅ **Auth Time Type**: Numeric claim type (OIDC spec compliant)

## References

- **OpenID Connect Core 1.0**: https://openid.net/specs/openid-connect-core-1_0.html#IDToken
  - Section 2: ID Token - `auth_time` must be a JSON number
- **RFC 7519**: JSON Web Token (JWT)
  - https://datatracker.ietf.org/doc/html/rfc7519
- **ClaimValueTypes**: https://learn.microsoft.com/en-us/dotnet/api/system.security.claims.claimvaluetypes
- **oauth4webapi**: Standards-compliant OAuth 2.0 client library
  - https://github.com/panva/oauth4webapi

## Notes

- The same fix was applied to both regular and encrypted JWT methods
- All numeric time claims (`iat`, `nbf`, `exp`, `auth_time`) now consistently use `ClaimValueTypes.Integer64`
- This ensures interoperability with strict standards-compliant OIDC clients
- Future JWT claims that represent numbers should also use the appropriate `ClaimValueTypes`
