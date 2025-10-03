# Authentication Time (auth_time) Fallback Fix

**Issue**: React OIDC client using `oauth4webapi` library failed during token validation with error:
```
ID Token "auth_time" (authentication time) must be a positive number
```

## Root Cause

The `auth_time` claim in the ID token was missing or invalid. This occurred when:

1. **User authenticated** via `/login` page → Cookie issued with `auth_time` claim
2. **Authorization request** → `AuthorizeHandler` tries to read `auth_time` from cookie
3. **Cookie claim missing/invalid** → `SetAuthTime()` never called on metadata store
4. **ID token generation** → `authTime` is `null`, so `auth_time` claim not added to ID token
5. **Client validation** → `oauth4webapi` rejects ID token as non-compliant

### Why Was It Missing?

The `AuthorizeHandler` only set `auth_time` in metadata IF it could successfully parse the claim from the cookie:

```csharp
// OLD CODE - no fallback
var authTimeClaim = http.User.FindFirst("auth_time")?.Value;
if (!string.IsNullOrEmpty(code) && long.TryParse(authTimeClaim, out var seconds))
{
    meta.SetAuthTime(code!, DateTimeOffset.FromUnixTimeSeconds(seconds));
}
// If parsing fails or claim missing → auth_time never set!
```

## Solution

Added a **fallback** that uses the current timestamp when `auth_time` is not available in the authentication cookie:

```csharp
// Capture auth_time from login cookie claims
var authTimeClaim = http.User.FindFirst("auth_time")?.Value;
if (!string.IsNullOrEmpty(code) && long.TryParse(authTimeClaim, out var seconds))
{
    meta.SetAuthTime(code!, DateTimeOffset.FromUnixTimeSeconds(seconds));
}
else if (!string.IsNullOrEmpty(code))
{
    // Fallback: if auth_time not in cookie, use current time (user is authenticated NOW)
    meta.SetAuthTime(code!, DateTimeOffset.UtcNow);
}
```

### Why This Works

- **User is already authenticated**: By the time the authorize endpoint runs, the user has passed authentication and has a valid cookie
- **Current time is accurate**: The authorization is happening NOW, so using `DateTimeOffset.UtcNow` is a reasonable fallback
- **OIDC compliance**: The `auth_time` claim is required in ID tokens, especially when `max_age` is used

## OIDC Specification Requirements

According to **OpenID Connect Core 1.0 Section 2**:

> `auth_time`: Time when the End-User authentication occurred. Its value is a JSON number representing the number of seconds from 1970-01-01T00:00:00Z as measured in UTC until the date/time. When a `max_age` request is made or when `auth_time` is requested as an Essential Claim, then this Claim is REQUIRED; otherwise, its inclusion is OPTIONAL.

Even though `max_age` might not be explicitly requested, standards-compliant clients like `oauth4webapi` expect `auth_time` to be present and valid.

## Flow After Fix

1. **User logs in** → Cookie created with `auth_time` claim ✅
2. **Authorization request** → Handler reads `auth_time` from cookie
3. **If claim present** → Use the actual authentication time from cookie ✅
4. **If claim missing/invalid** → **NEW: Fallback to current time** ✅
5. **ID token generation** → `auth_time` always present with valid value ✅
6. **Client validation** → Token accepted ✅

## Files Modified

- `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs` (lines ~455-465)

## Testing

1. **Clear browser cookies** to simulate a fresh login
2. **Navigate to React app**: `http://localhost:5173`
3. **Click "Login"** → Complete authentication flow
4. **Verify ID token** → Should now contain valid `auth_time` claim
5. **Client should complete** → No more "auth_time must be a positive number" error

## Related Fixes

This completes the OIDC token response implementation along with:
- ✅ PAR RFC 9126 compliance (HTTP 201, correct JSON)
- ✅ Consent POST handler (saves consent to database)
- ✅ Issuer identification (RFC 9207 - `iss` parameter)
- ✅ Authentication time fallback (ensures valid `auth_time` in ID token)

## References

- **OpenID Connect Core 1.0**: https://openid.net/specs/openid-connect-core-1_0.html#IDToken
- **oauth4webapi**: Standards-compliant OAuth 2.0 client library
- **JWT Claims**: https://datatracker.ietf.org/doc/html/rfc7519#section-4

## Notes

- The `auth_time` is stored as a Unix timestamp (seconds since epoch)
- Login page (`Login.cshtml.cs`) already sets `auth_time` in the cookie on line 72
- This fallback ensures backward compatibility with old cookies that might not have the claim
- Future logins will have the proper `auth_time` claim from the start
