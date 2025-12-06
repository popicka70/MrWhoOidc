# Quickstart: Global User Credentials

**Feature**: 008-global-user-credentials  
**Date**: 2025-12-05

## Overview

This guide explains how to implement and test the global user credentials feature, which transitions from per-tenant passwords to a single global credential per user.

## Prerequisites

- .NET 9 SDK installed
- PostgreSQL running (via Docker or Aspire)
- Feature flag `UserAccountDecouplingEnabled: true` in appsettings

## Quick Verification

### 1. Check Feature Flag Status

```json
// appsettings.Development.json
{
  "UserAccountFeatureOptions": {
    "UserAccountDecouplingEnabled": true,
    "TenantPickerUxEnabled": false
  }
}
```

### 2. Verify Existing Infrastructure

```bash
# Check UserAccount table exists
dotnet ef migrations list --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth

# Should show: AddUserAccountEntities (applied)
```

### 3. Run Existing Tests

```bash
# Verify current tests pass before changes
dotnet test --filter "FullyQualifiedName~UserServiceTests"
dotnet test --filter "FullyQualifiedName~DataIsolationTests"
```

## Implementation Steps

### Step 1: Add Schema Migration

```bash
# Add new fields to UserAccount
dotnet ef migrations add AddGlobalCredentialFields \
  --project MrWhoOidc.Auth \
  --startup-project MrWhoOidc.WebAuth \
  --output-dir Persistence/Migrations
```

**Migration should add**:

- `FailedLoginAttempts` (int, default 0)
- `LastFailedLoginAt` (DateTimeOffset?)
- `PasswordUpdatedAt` (DateTimeOffset?)

### Step 2: Extend UserAccountService

Add these methods to `IUserAccountService`:

```csharp
// Find by email
Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default);

// Find by username or email
Task<UserAccount?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken ct = default);

// Update password
Task UpdatePasswordAsync(Guid accountId, string hash, string? salt, string algorithm, CancellationToken ct = default);

// Get memberships
Task<IReadOnlyList<UserTenantMembership>> GetActiveMembershipsAsync(Guid accountId, CancellationToken ct = default);
```

### Step 3: Create GlobalAuthenticationService

```csharp
// MrWhoOidc.Auth/Services/GlobalAuthenticationService.cs
public interface IGlobalAuthenticationService
{
    Task<GlobalAuthenticationResult> AuthenticateAsync(string usernameOrEmail, string password, CancellationToken ct = default);
    Task RecordFailedAttemptAsync(Guid accountId, CancellationToken ct = default);
    Task ClearFailedAttemptsAsync(Guid accountId, CancellationToken ct = default);
    Task<bool> IsLockedOutAsync(Guid accountId, CancellationToken ct = default);
}
```

### Step 4: Register Services

```csharp
// MrWhoOidc.Auth/DependencyInjection.cs
services.AddScoped<IGlobalAuthenticationService, GlobalAuthenticationService>();
```

### Step 5: Update Login Handler

```csharp
// MrWhoOidc.WebAuth/Pages/Login.cshtml.cs
// Inject IGlobalAuthenticationService
// Replace per-tenant auth with global auth call
```

## Testing the Feature

### Unit Test: Global Authentication

```csharp
[TestMethod]
public async Task AuthenticateAsync_ValidCredentials_ReturnsSuccess()
{
    // Arrange
    var account = new UserAccount
    {
        Id = Guid.NewGuid(),
        Username = "testuser",
        PasswordHash = _hasher.Hash("password123").Hash
    };
    _db.UserAccounts.Add(account);
    await _db.SaveChangesAsync();

    // Act
    var result = await _authService.AuthenticateAsync("testuser", "password123");

    // Assert
    Assert.IsTrue(result.Succeeded);
    Assert.AreEqual(account.Id, result.Account!.Id);
}
```

### Integration Test: Cross-Tenant Login

```csharp
[TestMethod]
public async Task Login_SamePasswordWorksAcrossTenants()
{
    // Arrange: User with memberships in two tenants
    var account = CreateUserAccountWithMemberships("user@test.com", "Password1!", tenantA, tenantB);

    // Act: Login via Tenant A
    var resultA = await LoginAsync(tenantA.Slug, "user@test.com", "Password1!");
    
    // Act: Login via Tenant B
    var resultB = await LoginAsync(tenantB.Slug, "user@test.com", "Password1!");

    // Assert: Same password works for both
    Assert.IsTrue(resultA.IsSuccess);
    Assert.IsTrue(resultB.IsSuccess);
}
```

### Integration Test: Password Change Propagates

```csharp
[TestMethod]
public async Task PasswordChange_AffectsAllTenants()
{
    // Arrange
    var account = CreateUserAccountWithMemberships("user@test.com", "OldPassword!", tenantA, tenantB);

    // Act: Change password via Tenant A profile
    await ChangePasswordAsync(tenantA.Slug, "user@test.com", "OldPassword!", "NewPassword!");

    // Assert: New password works for Tenant B
    var result = await LoginAsync(tenantB.Slug, "user@test.com", "NewPassword!");
    Assert.IsTrue(result.IsSuccess);

    // Assert: Old password fails for both tenants
    var failA = await LoginAsync(tenantA.Slug, "user@test.com", "OldPassword!");
    var failB = await LoginAsync(tenantB.Slug, "user@test.com", "OldPassword!");
    Assert.IsFalse(failA.IsSuccess);
    Assert.IsFalse(failB.IsSuccess);
}
```

## Migration Verification

### Backfill Script Verification

```sql
-- Check UserAccounts created from Users
SELECT 
    ua.Id,
    ua.Username,
    ua.Email,
    COUNT(utm.Id) as MembershipCount
FROM "UserAccounts" ua
LEFT JOIN "UserTenantMemberships" utm ON ua.Id = utm.UserAccountId
GROUP BY ua.Id, ua.Username, ua.Email;

-- Check for orphaned Users (should have matching UserAccount)
SELECT u.Id, u.Username, u.TenantId
FROM "Users" u
LEFT JOIN "UserAccounts" ua ON u.Id = ua.Id
WHERE ua.Id IS NULL;
```

## Troubleshooting

### Issue: Login fails after migration

**Check**:

1. UserAccount exists for the user
2. PasswordHash was copied correctly
3. Membership exists for the tenant

```sql
SELECT ua.*, utm.TenantId, utm.Status
FROM "UserAccounts" ua
JOIN "UserTenantMemberships" utm ON ua.Id = utm.UserAccountId
WHERE ua.Username = 'affected_user';
```

### Issue: Different passwords existed

**Resolution**: User should use the most recent password. Check audit log:

```bash
grep "password conflict" /var/log/mrwhooidc/migration.log
```

### Issue: Account locked globally

**Check lockout status**:

```sql
SELECT Id, Username, LockedOutUntil, FailedLoginAttempts
FROM "UserAccounts"
WHERE Username = 'locked_user';
```

**Reset lockout** (admin action):

```sql
UPDATE "UserAccounts"
SET LockedOutUntil = NULL, FailedLoginAttempts = 0
WHERE Username = 'locked_user';
```

## Feature Flags

| Flag | Purpose | Default |
|------|---------|---------|
| `UserAccountDecouplingEnabled` | Enable global auth reads | `true` |
| `DualWriteEnabled` | Write to both User and UserAccount | `true` during migration |
| `LegacyAuthFallback` | Fall back to User auth if Account not found | `true` during migration |

## Rollback Plan

If issues occur:

1. Set `UserAccountDecouplingEnabled: false`
2. Authentication falls back to per-tenant `User` table
3. No data loss—`User` table still has credentials
4. Investigate and fix before re-enabling
