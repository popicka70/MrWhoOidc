# Standard Scopes IsGlobal Fix

**Issue Date:** October 12, 2025  
**Migration:** `20251012064228_FixStandardScopesIsGlobal`

## Problem

After implementing tenant-scoped scopes (Phase 1-3), tenant admins could not select standard OIDC scopes (openid, profile, email, etc.) when configuring clients. The scope dropdown only showed tenant-specific scopes.

### Root Cause

The standard OIDC scopes were created in the database before the tenant-scoped scopes feature was implemented. When the `IsGlobal` and `TenantId` columns were added to the `Scopes` table, existing scopes defaulted to:
- `IsGlobal = false`
- `TenantId = NULL` (or specific tenant)

This caused the `ScopeResolver.GetAvailableScopesAsync` method to filter out these scopes for tenant contexts, even though they should be available globally.

### Expected Behavior

Standard OIDC scopes should be:
- `IsGlobal = true` 
- `TenantId = NULL`
- Available to all tenants

Standard scopes include:
- openid
- profile  
- email
- address
- phone
- offline_access
- roles

## Solution

Created data migration `FixStandardScopesIsGlobal` that updates existing standard scopes:

```sql
UPDATE "Scopes"
SET "IsGlobal" = true, "TenantId" = NULL
WHERE "Name" IN ('openid', 'profile', 'email', 'address', 'phone', 'offline_access', 'roles')
  AND "IsGlobal" = false;
```

## Migration Details

**File:** `MrWhoOidc.Auth/Persistence/Migrations/20251012064228_FixStandardScopesIsGlobal.cs`

**Key Operations:**
- Sets `IsGlobal = true` for standard scopes
- Sets `TenantId = NULL` to ensure global availability
- Only updates scopes where `IsGlobal = false` to be idempotent

**Down Migration:**
- No-op (reversing would be problematic as we don't know original tenant ownership)
- Re-seeding would be needed to restore original state if rollback required

## Verification

After migration, verify scopes table:

```sql
SELECT "Name", "IsGlobal", "TenantId" 
FROM "Scopes" 
ORDER BY "IsGlobal" DESC, "Name";
```

Expected output:
```
      Name      | IsGlobal | TenantId 
----------------+----------+----------
 email          | t        |
 offline_access | t        |
 openid         | t        |
 profile        | t        |
 roles          | t        |
 tenant.scope   | f        | <uuid>
```

## Testing

1. Login as tenant admin
2. Navigate to Admin → Clients → Edit Client → Scopes tab
3. Click "Add scope" dropdown
4. Verify dropdown shows:
   - **Global scopes:** openid, profile, email, offline_access, roles
   - **Tenant scopes:** [tenant-slug].[suffix] format scopes

## Related Files

- `MrWhoOidc.Auth/Services/ScopeResolver.cs` - Scope resolution logic
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs` - Client edit page with scope assignment
- `MrWhoOidc.Auth/Persistence/Migrations/20251011200133_AddTenantScopedScopes.cs` - Original tenant scopes migration

## Prevention

For future deployments:
1. Initial scope seeding should explicitly set `IsGlobal = true` for standard scopes
2. Consider adding a database constraint or check to enforce standard scope flags
3. Add unit tests to verify standard scopes are always marked as global

## Related Documentation

- [Tenant Scoped Scopes Complete](./tenant-scoped-scopes-complete.md) - Full feature documentation
- [PostgreSQL Migration Syntax Fix](./postgresql-migration-syntax-fix.md) - Related migration fix
