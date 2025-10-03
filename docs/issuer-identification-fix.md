# Issuer Identification (RFC 9207) Fix

**Issue**: React OIDC client using `oauth4webapi` library failed during callback processing with error:
```
response parameter "iss" (issuer) missing
```

The authorization redirect was:
```
http://localhost:5173/callback?code=...&nonce=...&state=...
```

But it was missing the `iss` parameter.

## Root Cause

The `AuthorizeHandler.cs` was building the authorization response redirect without including the **issuer identification parameter** (`iss`). This parameter is required by:

- **RFC 9207**: OAuth 2.0 Authorization Server Issuer Identification
- **oauth4webapi**: Strict RFC compliance validation

The `iss` parameter helps prevent **authorization server mix-up attacks** where a malicious actor might trick a client into accepting an authorization code from a different authorization server.

### Previous Code

```csharp
// Only added state, no iss parameter
if (!string.IsNullOrEmpty(effectiveReq.state))
{
    var uri = new UriBuilder(redirect);
    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
    query["state"] = effectiveReq.state;
    uri.Query = query.ToString();
    return Results.Redirect(uri.ToString());
}

return Results.Redirect(redirect);
```

## Solution

Modified the authorization response redirect to **always include the `iss` parameter** along with the authorization code and state:

```csharp
// RFC 9207: Add issuer identification parameter to prevent mix-up attacks
var iss = GetIssuer(http);
var uri2 = new UriBuilder(redirect);
var query2 = System.Web.HttpUtility.ParseQueryString(uri2.Query);
query2["iss"] = iss;
if (!string.IsNullOrEmpty(effectiveReq.state))
{
    query2["state"] = effectiveReq.state;
}
uri2.Query = query2.ToString();
return Results.Redirect(uri2.ToString());
```

### How It Works

1. **Get issuer**: Calls `GetIssuer(http)` which returns `https://localhost:7208` (or the actual host)
2. **Parse existing redirect**: The `redirect` variable already contains `redirect_uri?code=...&nonce=...`
3. **Add iss parameter**: Appends `&iss=https://localhost:7208`
4. **Add state if present**: Appends `&state=...` if the original request had state
5. **Redirect**: Returns the complete URL back to the client

## RFC-Compliant Authorization Response

**Before:**
```
http://localhost:5173/callback?code=F3ZNdDoi5fAC69JC4ENF14QpL_DF0VoqBW0rsmKnXtw&nonce=ucUKnnSEsonXLUuDvGVHjbzEF2oPAYDmODS4kZYAtR0&state=8h3phfXvVrpCMAD_QAJjWQo1fEINfoqQNoibsXDEKGw
```

**After:**
```
http://localhost:5173/callback?code=F3ZNdDoi5fAC69JC4ENF14QpL_DF0VoqBW0rsmKnXtw&nonce=ucUKnnSEsonXLUuDvGVHjbzEF2oPAYDmODS4kZYAtR0&iss=https%3A%2F%2Flocalhost%3A7208&state=8h3phfXvVrpCMAD_QAJjWQo1fEINfoqQNoibsXDEKGw
```

## Security Benefits

The `iss` parameter prevents **mix-up attacks**:

1. **Client requests authorization** from `https://legitimate-op.com`
2. **Attacker intercepts** and redirects to malicious OP `https://evil-op.com`
3. **Without iss**: Client might accept code from evil-op
4. **With iss**: Client validates `iss` matches expected issuer and rejects the code

## Files Modified

- `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs` (lines ~500-510)

## Testing

1. Build: `dotnet build` ✅
2. Start server: `dotnet run --project MrWhoOidc.AppHost` ✅
3. Navigate to React app: `http://localhost:5173` ✅
4. Click "Login" → Complete flow → Check callback URL ✅
5. Verify `iss` parameter present in redirect ✅

## Related Fixes

This completes the OIDC authorization code flow implementation along with:
- ✅ PAR RFC 9126 compliance (HTTP 201, correct JSON)
- ✅ Consent POST handler (saves consent to database)
- ✅ Issuer identification (RFC 9207)

## References

- **RFC 9207**: OAuth 2.0 Authorization Server Issuer Identification
  - https://datatracker.ietf.org/doc/html/rfc9207
- **oauth4webapi**: Standards-compliant OAuth 2.0 client library
  - https://github.com/panva/oauth4webapi
- **OIDC Core Spec**: https://openid.net/specs/openid-connect-core-1_0.html

## Notes

- The `iss` parameter is URL-encoded in the query string
- Both JARM responses and regular redirects now comply with RFC 9207
- This is a **required** security feature for modern OIDC implementations
