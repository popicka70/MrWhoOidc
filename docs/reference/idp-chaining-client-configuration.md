# IdP Chaining Client Configuration Guide

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

**Problem:** The client has `AutoRedirectIfSingle = true` on its only provider mapping and has both local and QR login disabled, so IdP #2 redirects straight to that provider.

**Solution:** Set `AutoRedirectIfSingle = false`, or enable local/QR login, if you want the picker shown.

## Advanced: Propagating Hints

The current implementation supports hint propagation across IdP chains via `ExternalOidcUrlHelpers.CopyHintsFromUrl`. This means:

- `login_hint`, `acr_values`, `prompt`, `max_age` and `ui_locales` from the original request are forwarded from IdP #1 to IdP #2
- `resource` and `audience` are forwarded too (IdP #2 only accepts `resource` values in its `Auth:ApiAudiences` or the client's `M2MAllowedAudiencesJson`)
- No other parameters are copied

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

3. **Check the authorization response:** `access_denied` with `No permitted login methods for this client` means local login is disabled and no QR/provider path is available.

### Issue: Provider Picker Shows No Options

This means the client has `AllowLocalLogin = false`, `AllowQrLogin = false`, and no external providers mapped. The authorize flow returns `access_denied` (`No permitted login methods for this client`).

**Solution:** Enable at least one login method or map at least one external provider.

## Architecture Notes

Login method selection lives in `MrWhoOidc.Auth/Services/Authorization/ProviderSelectionService.cs` (called from the authorize flow):

1. Load the client configuration (`AllowLocalLogin`, `AllowExternalIdp`, `AllowQrLogin`) and its enabled provider mappings
2. Explicit `idp` parameter matching a mapped provider → redirect to it
3. `idp_hint` matching a mapped provider (and account selection not forced) → redirect to it
4. Exactly one provider with `AutoRedirectIfSingle`, local and QR login disabled, account selection not forced → redirect
5. Last-used provider cookie matches a mapped provider, local and QR login disabled, account selection not forced → redirect
6. Otherwise show the picker if there are providers or QR is enabled; else fall back to local login if allowed

The key insight is that **each client controls its own login method policy**, including clients representing upstream IdPs in a chaining scenario.

## Related Documentation

- [Admin Guide](../admin-guide.md) - Provider and client configuration reference
- [Developer Guide](../developer-guide.md) - Integration patterns
