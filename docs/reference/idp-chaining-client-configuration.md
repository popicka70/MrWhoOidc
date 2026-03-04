# IdP Chaining Client Configuration Guide

> **⚠️ URL Convention Change (November 2025)**  
> Admin URLs now use kebab-case (e.g., `/admin/clients` instead of `/Admin/Clients`). This guide reflects the new convention.

## Problem

When chaining two MrWhoOidc IdP instances (IdP #1 → IdP #2), the second IdP may bypass its provider picker and go directly to username/password login, skipping configured login options like QR code and other external IdPs.

## Root Cause

When IdP #1 redirects to IdP #2 for authentication, it acts as an **OAuth/OIDC client** to IdP #2. The client configuration in IdP #2's database controls which login methods are available during that authentication flow.

## How IdP Chaining Works

```
User → Blazor App → IdP #1 → IdP #2
                     (client)  (authorization server)
```

- **Blazor App** is registered as a client in **IdP #1**
- **IdP #1** is registered as an external OIDC provider in itself (if it has other IdPs)
- **IdP #1** is registered as a **client** in **IdP #2** (via the OIDC provider config's `ClientId`)

## Solution: Configure the Chained IdP's Client Settings

In **IdP #2**, you need to properly configure the client that represents **IdP #1**.

### Step 1: Find the Client in IdP #2

1. Navigate to IdP #2's Admin UI
2. Go to **Admin → Clients** (route: `/admin/clients`)
3. Find the client with `ClientId` matching the `ClientId` configured in IdP #1's external provider settings

### Step 2: Enable Login Methods

Edit the client configuration and ensure these flags are set correctly:

| Setting | Description | Recommended for IdP Chaining |
|---------|-------------|------------------------------|
| **Allow local username/password login** | Shows the local login form | ✅ Enable if IdP #2 has local users |
| **Allow external identity providers** | Shows external IdP options in provider picker | ✅ Enable if IdP #2 has other external IdPs |
| **Allow QR code login** | Shows QR login option | ✅ Enable if IdP #2 has QR login configured |

### Step 3: Configure Client-to-Provider Mappings

If IdP #2 has external providers configured, you need to map them to the client representing IdP #1:

1. Go to **Admin → Provider Mappings** (route: `/admin/provider-mappings`)
2. Find or create mappings for the client representing IdP #1
3. Add the external providers you want to be available during IdP chaining
4. Set `Order`, `AutoRedirectIfSingle`, and other flags as needed

## Example Configuration

### Scenario: Two-Level IdP Chaining with QR Support

- **IdP #1** (`https://idp1.example.com`): Has one external provider pointing to IdP #2
- **IdP #2** (`https://idp2.example.com`): Has QR login, local login, and another external IdP (e.g., Azure AD)

#### IdP #1 External Provider Configuration

In IdP #1, create an external OIDC provider:

```json
{
  "Name": "idp2",
  "DisplayName": "Corporate IdP",
  "Type": "OIDC",
  "Authority": "https://idp2.example.com",
  "ClientId": "idp1-client",
  "ClientSecret": "secret-value",
  "Scopes": ["openid", "profile", "email"],
  "UsePKCE": true
}
```

#### IdP #2 Client Configuration

In IdP #2, ensure the client with `ClientId = "idp1-client"` has:

```
✅ Allow local username/password login
✅ Allow external identity providers
✅ Allow QR code login
```

#### IdP #2 Provider Mappings

Map IdP #2's external providers to the `idp1-client`:

| Client | Provider | Enabled | Order |
|--------|----------|---------|-------|
| idp1-client | Azure AD | ✅ | 1 |
| idp1-client | Google | ✅ | 2 |

## Verification

After configuration, test the flow:

1. Log into the Blazor app
2. It should redirect to IdP #1
3. Select the "Corporate IdP" provider (IdP #2)
4. **IdP #2 should now show:**
   - Local login option
   - QR code option (if enabled)
   - Azure AD option
   - Google option
   - Any other configured providers

## Common Mistakes

### ❌ Mistake 1: Default Client Has Restrictive Settings

**Problem:** The client representing IdP #1 in IdP #2 was auto-created or manually created with default settings that disable external IdPs.

**Solution:** Explicitly enable all login methods you want available.

### ❌ Mistake 2: No Provider Mappings

**Problem:** IdP #2 has external providers configured, but they're not mapped to the client representing IdP #1.

**Solution:** Go to **Admin → Provider Mappings** (route: `/admin/provider-mappings`) and add mappings.

### ❌ Mistake 3: Auto-Redirect Settings

**Problem:** The client has `AutoRedirectIfSingle = true` on a provider mapping, causing IdP #2 to skip the picker when only one provider is mapped.

**Solution:** Set `AutoRedirectIfSingle = false` if you want to show all options even when there's only one external provider.

## Advanced: Propagating Hints

The current implementation supports hint propagation across IdP chains via `ExternalOidcUrlHelpers.CopyHintsFromUrl`. This means:

- `login_hint` from the Blazor app will flow through IdP #1 → IdP #2
- `acr_values` will propagate
- `prompt` will propagate
- Other standard OIDC parameters will propagate

These hints are automatically included in the authorization request from IdP #1 to IdP #2.

## Troubleshooting

### Issue: Still Going Directly to Login

1. **Check the client configuration:**
   ```sql
   SELECT "ClientId", "AllowLocalLogin", "AllowExternalIdp", "AllowQrLogin"
   FROM "Clients"
   WHERE "ClientId" = 'idp1-client';
   ```

2. **Check provider mappings:**
   ```sql
   SELECT c."ClientId", ip."Name", cip."Enabled", cip."Order"
   FROM "ClientIdentityProviders" cip
   JOIN "Clients" c ON c."Id" = cip."ClientId"
   JOIN "IdentityProviders" ip ON ip."Id" = cip."IdentityProviderId"
   WHERE c."ClientId" = 'idp1-client';
   ```

3. **Check the authorize handler logs:**
   Look for log entries showing:
   - `allowExternal = false` (indicates external IdPs are disabled)
   - `allowLocal = false` and no provider mappings (would cause access_denied)
   - `providerLinks.Count = 0` (no providers mapped)

### Issue: Provider Picker Shows No Options

This means the client has `AllowLocalLogin = false`, `AllowQrLogin = false`, and no external providers mapped. The authorize handler will return `access_denied`.

**Solution:** Enable at least one login method or map at least one external provider.

## Architecture Notes

The login method selection logic in `AuthorizeHandler.cs` (lines 270-400) follows this flow:

1. Validate the authorization request
2. Load the client configuration (`AllowLocalLogin`, `AllowExternalIdp`, `AllowQrLogin`)
3. Check for explicit `idp` parameter (skip picker if present and allowed)
4. Check for QR parameter (initiate QR flow if allowed)
5. If unauthenticated:
   - If `idp_hint` matches an available provider → redirect to that provider
   - If single provider and `AutoRedirectIfSingle` → redirect
   - If last-used provider exists → redirect (unless `prompt=select_account`)
   - Otherwise → show provider picker
6. Fallback to local login if allowed

The key insight is that **each client controls its own login method policy**, including clients representing upstream IdPs in a chaining scenario.

## Related Documentation

- [IdP Chaining Backlog](../done/idp-chaining-backlog.md) - Feature implementation status
- [Admin Guide](../admin-guide.md) - Provider and client configuration reference
- [Developer Guide](../developer-guide.md) - Integration patterns

## Future Enhancements

Potential improvements to IdP chaining UX (tracked in backlog):

1. **Auto-configure chained IdP clients:** When creating an external OIDC provider, offer to auto-create a properly configured client on the upstream IdP
2. **Inheritance hints:** Allow a client to "inherit" login method settings from a parent/default configuration
3. **Per-provider login method overrides:** Allow specific providers to enforce certain login methods (e.g., always show local login when coming from a specific upstream IdP)
4. **Visual indication in Admin UI:** Show which clients are used by external providers to make chaining relationships more visible
