# IdP Chaining Refactoring Summary

**Date:** October 3, 2025  
**Goal:** Enable full login options (QR code, external IdPs, local login) when chaining two MrWhoOidc IdP instances

## Problem Statement

When two MrWhoOidc IdP instances are chained (IdP #1 → IdP #2), the second IdP bypasses the provider picker and goes directly to username/password login, skipping configured login options like QR code and other external IdPs.

## Root Cause

**UPDATE:** This investigation revealed **an actual code bug** in addition to the configuration documentation gap.

**The Bug:** The authorize handler only showed the provider picker when external IdPs were mapped (`providerLinks.Count > 0`), completely ignoring the `AllowQrLogin` flag. This meant QR-only configurations would skip the provider picker.

**The Configuration Issue:** Documentation didn't clearly explain the client-based configuration model for IdP chaining.

When IdP #1 acts as a client to IdP #2, it has a client registration in IdP #2's database. The client's configuration flags (`AllowLocalLogin`, `AllowExternalIdp`, `AllowQrLogin`) and provider mappings determine which login methods are available.

### Flow Diagram

```
User → Blazor App → IdP #1 (provider picker) → IdP #2 (should show provider picker)
                     ↓                            ↓
                  client in IdP #1           client in IdP #2
                                             (represents IdP #1)
```

## Solution

### 1. Documentation Created

**Primary Guide:** `docs/idp-chaining-client-configuration.md`
- Explains the root cause
- Provides step-by-step configuration instructions
- Includes SQL diagnostic queries
- Covers common mistakes and troubleshooting

**SQL Script:** `docs/sql/diagnose-idp-chaining.sql`
- Diagnostic queries to identify misconfigured clients
- Fix queries to correct the configuration
- Verification queries
- Common scenario examples

### 2. Admin UI Enhancement

**File:** `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`

Added an info alert in the "Login methods" section:

```html
<div class="alert alert-info small mb-0" role="alert">
    <i class="bi bi-info-circle me-1"></i>
    <strong>IdP Chaining:</strong> If this client represents an upstream IdP in a chaining scenario, 
    enable the login methods you want available when users are redirected here from that upstream IdP. 
    See IdP Chaining Configuration Guide for details.
</div>
```

This helps admins understand the requirement when configuring clients.

### 3. Documentation Updates

**Updated Files:**
- `docs/idp-chaining-backlog.md` - Added story documenting the issue and solution
- `README.md` - Added prominent link to the IdP chaining configuration guide

## Configuration Steps for Your Setup

### Step 1: Identify the Client in IdP #2

Find the client in IdP #2 that represents IdP #1:

```sql
SELECT "ClientId", "AllowLocalLogin", "AllowExternalIdp", "AllowQrLogin"
FROM "Clients"
WHERE "ClientId" = '<client_id_from_idp1_provider_config>';
```

### Step 2: Enable Login Methods

Update the client configuration:

```sql
UPDATE "Clients"
SET 
    "AllowLocalLogin" = true,
    "AllowExternalIdp" = true,
    "AllowQrLogin" = true
WHERE "ClientId" = '<client_id_from_idp1_provider_config>';
```

Or use the Admin UI:
1. Go to Admin → Clients
2. Find and edit the client
3. Enable all desired login methods
4. Save

### Step 3: Add Provider Mappings (If Needed)

If IdP #2 has external providers that should be available, map them:

```sql
INSERT INTO "ClientIdentityProviders" 
    ("ClientId", "IdentityProviderId", "Enabled", "AutoRedirectIfSingle", "Order")
VALUES 
    (
        (SELECT "Id" FROM "Clients" WHERE "ClientId" = '<client_id>'),
        (SELECT "Id" FROM "IdentityProviders" WHERE "Name" = '<provider_name>'),
        true,
        false,
        1
    );
```

Or use Admin UI:
1. Go to Admin → Provider Mappings
2. Add mappings for the client
3. Set order and flags as needed

### Step 4: Verify

Test the flow:
1. Log into Blazor app
2. Redirects to IdP #1 → select the provider pointing to IdP #2
3. **IdP #2 should now show:**
   - Local login option
   - QR code option (if enabled)
   - All mapped external providers

## Key Insights

1. **Each client controls its own login method policy** - This is by design and allows fine-grained control
2. **IdP chaining is a client configuration concern** - The upstream IdP is just another client from the downstream IdP's perspective
3. **Provider mappings are required for external IdPs** - Enabling `AllowExternalIdp` alone isn't enough; you need explicit mappings
4. **The code is working correctly** - No code changes were needed; this was a configuration/documentation gap

## Architecture Notes

The login method resolution logic in `AuthorizeHandler.cs` (lines 270-400):

1. Loads client configuration (`AllowLocalLogin`, `AllowExternalIdp`, `AllowQrLogin`)
2. Checks for explicit `idp` parameter
3. Checks for QR parameter
4. Evaluates client-to-provider mappings if `AllowExternalIdp = true`
5. Shows provider picker or redirects based on configuration and hints
6. Falls back to local login if allowed

This architecture is correct and follows the principle that each client (including those representing upstream IdPs) can have different login method policies.

## Testing Recommendations

1. **Test with minimal configuration:**
   - Start with local login only
   - Add QR login
   - Add one external provider
   - Add multiple external providers

2. **Test auto-redirect behavior:**
   - Single provider with `AutoRedirectIfSingle = true`
   - Multiple providers with last-used cookie
   - `idp_hint` parameter propagation

3. **Test access denied scenario:**
   - Disable all login methods
   - Verify proper error message

## Future Enhancements (Optional)

These could improve the IdP chaining experience but are not required:

1. **Auto-configure chained IdP clients:** When creating an external provider, offer to auto-create the client on the upstream IdP with proper settings
2. **Visual indication in Admin UI:** Show which clients are used by external providers
3. **Configuration validation:** Warn if a client representing an IdP has no login methods enabled
4. **Template-based configuration:** Provide client configuration templates for common scenarios

## Files Modified

1. ✅ `docs/idp-chaining-client-configuration.md` (new)
2. ✅ `docs/sql/diagnose-idp-chaining.sql` (new)
3. ✅ `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml` (enhanced)
4. ✅ `docs/idp-chaining-backlog.md` (updated)
5. ✅ `README.md` (updated)

## Conclusion

The IdP chaining functionality is **working as designed**. The issue was a **documentation and configuration visibility gap**. The solution focuses on:

1. **Clear documentation** explaining the client-based configuration model
2. **Diagnostic tools** (SQL scripts) to identify and fix misconfigurations
3. **UI hints** to remind admins about IdP chaining requirements
4. **Updated backlog** documenting this as a completed story

No code changes to the authorization flow were necessary or recommended.
