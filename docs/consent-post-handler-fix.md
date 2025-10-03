# Consent Page POST Handler Fix

**Issue**: After clicking "Allow Access" on the consent page (`/consent`), the page would refresh instead of redirecting back to the calling application. The user would get stuck in a loop on the consent page.

## Root Cause

The `ConsentModel` page model in `MrWhoOidc.WebAuth/Pages/Consent.cshtml.cs` had **no POST handler** (`OnPostAsync` method). When the consent form was submitted via POST:

1. ASP.NET Core Razor Pages would find no matching handler
2. The page would simply reload (GET request)
3. No consent was saved to the database
4. The user remained on the consent screen

## Solution

Added an `OnPostAsync` handler to the `ConsentModel` class that:

1. **Validates form inputs**: Checks that `ReturnUrl`, `ClientId`, and `Scopes` are provided
2. **Extracts user ID**: Gets the authenticated user's ID from claims (`ClaimTypes.NameIdentifier`)
3. **Grants consent**: Calls `IConsentService.GrantConsentAsync()` to save consent to database
4. **Redirects back**: Returns `Redirect(ReturnUrl)` to continue the authorization flow

### Code Added

```csharp
public async Task<IActionResult> OnPostAsync()
{
    if (string.IsNullOrEmpty(ReturnUrl))
    {
        return BadRequest("Missing ReturnUrl");
    }

    if (string.IsNullOrEmpty(ClientId))
    {
        return BadRequest("Missing ClientId");
    }

    if (Scopes == null || Scopes.Length == 0)
    {
        return BadRequest("Missing Scopes");
    }

    // Get the current user's ID from claims
    var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
    {
        return Unauthorized();
    }

    // Grant consent
    await consentService.GrantConsentAsync(userId, ClientId, Scopes);

    // Redirect back to the authorize endpoint (ReturnUrl already contains the full query string)
    return Redirect(ReturnUrl);
}
```

### Dependencies Injected

Added constructor parameter injection:
```csharp
public class ConsentModel(IConsentService consentService) : PageModel
```

Added using statement:
```csharp
using System.Security.Claims;
```

## Authorization Flow After Fix

1. **User logs in** → Authenticated with cookie
2. **App requests authorization** → `/authorize` endpoint with PAR `request_uri`
3. **Consent required** → User redirected to `/consent` page (GET)
4. **User clicks "Allow Access"** → Form POSTed to `/consent`
5. **✅ NEW: OnPostAsync handler** → Consent saved to database
6. **✅ Redirect to ReturnUrl** → Back to `/authorize` with original parameters
7. **Consent check passes** → Authorization code generated
8. **Redirect to app** → User returned to React app with `code` and `state`

## Testing

1. Build: `dotnet build` ✅
2. Start server: `dotnet run --project MrWhoOidc.AppHost` ✅
3. Navigate to React app: `http://localhost:5173` ✅
4. Click "Login" → PAR request succeeds → Redirected to consent page ✅
5. Click "Allow Access" → Consent saved → Redirected back to app ✅

## Files Modified

- `MrWhoOidc.WebAuth/Pages/Consent.cshtml.cs` - Added `OnPostAsync` handler

## Related Issues

This fix completes the OIDC authorization code flow implementation for the React demo client. Previously, the flow would break at the consent step, preventing successful authentication.

## References

- **ConsentService**: `MrWhoOidc.Auth/Services/ConsentService.cs`
- **AuthorizeHandler**: Checks consent via `IConsentService.HasConsentAsync()`
- **ASP.NET Core Razor Pages**: POST handlers must be explicitly defined as `OnPostAsync` or `OnPost`
