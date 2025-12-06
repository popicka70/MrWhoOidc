# Global Credentials Migration Guide

This document describes how to migrate existing per-tenant user passwords to the new global `UserAccount` credentials model.

## Overview

Prior to this change, user passwords were stored per-tenant in the `User` entity. This was counterintuitive because users expected a single password across all tenants. The new model stores passwords in the global `UserAccount` entity.

## Migration Strategies

### 1. On-Demand Migration (Automatic)

The `UserAccountProvisioner` automatically migrates users during login. When a user authenticates:

1. If they have a `UserAccount` with no password
2. The system finds the most recently created per-tenant `User` with a password
3. Copies that password to `UserAccount.PasswordHash`

**Pros**: Zero downtime, gradual migration
**Cons**: Inactive users remain unmigrated until they log in

### 2. Batch Migration via Admin API

Use the platform-admin API endpoints to migrate users in batches.

#### API Endpoints

All endpoints require `platform-admin` authorization.

##### Get Migration Status

```http
GET /platform-admin/api/migrate-credentials/status
```

Response:
```json
{
  "totalAccounts": 1500,
  "migratedCount": 1200,
  "unmigratedCount": 300,
  "percentComplete": 80.0
}
```

##### Migrate a Batch of Users

```http
POST /platform-admin/api/migrate-credentials
Content-Type: application/json

{
  "batchSize": 100
}
```

Response:
```json
{
  "processedCount": 100,
  "successCount": 95,
  "failureCount": 0,
  "skippedCount": 5,
  "durationMs": 1234
}
```

- `batchSize`: Number of users to process (1-1000, default: 100)
- `skippedCount`: Users already migrated or with no password to migrate

##### Migrate a Single User

```http
POST /platform-admin/api/migrate-credentials/{accountId}
```

Response:
```json
{
  "success": true,
  "skipped": false,
  "affectedTenants": 2,
  "message": "Password migrated from tenant 'tenant-a'"
}
```

### 3. Direct SQL Migration (Advanced)

For very large databases, a direct SQL migration may be faster. **Always back up first.**

```sql
-- Preview: Find users needing migration
SELECT ua.id, ua.email, 
       COUNT(u.id) AS tenant_count,
       MAX(u.created_at) AS newest_user_created
FROM user_accounts ua
LEFT JOIN user_tenant_memberships utm ON utm.user_account_id = ua.id
LEFT JOIN users u ON u.email = ua.email AND u.tenant_id = utm.tenant_id
WHERE ua.password_hash IS NULL
  AND u.password_hash IS NOT NULL
GROUP BY ua.id, ua.email;

-- Migration: Copy most recent password per account
WITH ranked_passwords AS (
    SELECT 
        ua.id AS account_id,
        u.password_hash,
        ROW_NUMBER() OVER (
            PARTITION BY ua.id 
            ORDER BY u.created_at DESC
        ) AS rn
    FROM user_accounts ua
    INNER JOIN user_tenant_memberships utm ON utm.user_account_id = ua.id
    INNER JOIN users u ON u.email = ua.email AND u.tenant_id = utm.tenant_id
    WHERE ua.password_hash IS NULL
      AND u.password_hash IS NOT NULL
)
UPDATE user_accounts
SET password_hash = rp.password_hash,
    updated_at = NOW()
FROM ranked_passwords rp
WHERE user_accounts.id = rp.account_id
  AND rp.rn = 1;
```

## Migration Logic

The migration service uses this logic to select which password to copy:

1. **If `UserAccount.PasswordHash` is not null**: Skip (already migrated)
2. **Find linked tenants** via `UserTenantMembership`
3. **Find matching `User` records** by email in those tenants
4. **Select the most recently created `User`** that has a password
5. **Copy `User.PasswordHash` → `UserAccount.PasswordHash`**

### Why "Most Recent"?

Users who changed their password in one tenant likely want that password. The most recently created tenant membership is assumed to have the most current password preference.

## Post-Migration Cleanup (Optional)

After all users are migrated and verified, you may optionally clear per-tenant passwords:

```sql
-- ONLY after verifying all UserAccounts have passwords!
UPDATE users
SET password_hash = NULL,
    updated_at = NOW()
WHERE password_hash IS NOT NULL;
```

**⚠️ Warning**: This is irreversible. Ensure all UserAccounts are properly migrated first.

## Verification

### Check Migration Progress

```sql
SELECT 
    COUNT(*) FILTER (WHERE password_hash IS NOT NULL) AS migrated,
    COUNT(*) FILTER (WHERE password_hash IS NULL) AS unmigrated,
    ROUND(100.0 * COUNT(*) FILTER (WHERE password_hash IS NOT NULL) / COUNT(*), 2) AS percent
FROM user_accounts;
```

### Test a Specific User

1. Get their `UserAccount.Id`
2. Call `GET /platform-admin/api/migrate-credentials/status` 
3. Verify they can log in to each of their tenants

## Rollback

If issues occur, passwords remain in the per-tenant `User` table. To rollback:

1. Clear `UserAccount.PasswordHash` for affected accounts
2. The on-demand migration will re-migrate on next login

```sql
-- Rollback specific user
UPDATE user_accounts
SET password_hash = NULL
WHERE id = 'specific-account-id';

-- Nuclear rollback (rare!)
UPDATE user_accounts
SET password_hash = NULL
WHERE password_hash IS NOT NULL;
```

## Related Files

- `MrWhoOidc.Auth/Services/PasswordMigrationService.cs` - Migration service implementation
- `MrWhoOidc.Auth/Services/UserAccountProvisioner.cs` - On-demand migration during login
- `MrWhoOidc.Auth/Services/GlobalAuthenticationService.cs` - Global authentication logic
- `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs` - Admin API endpoints

## See Also

- [Client Secret Rotation Guide](./client-secret-rotation-guide.md) - Similar migration pattern
- [Admin Guide](./admin-guide.md) - General administration
