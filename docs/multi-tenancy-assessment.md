# Multi-Tenancy Deep Dive Assessment

> **Historical review; not a current defect inventory.** Reproduce each finding against current tenant resolution, authorization, and persistence before changing code. No findings were declared fixed by the 2026-09-05 documentation review. Track current verification in [documentation status](documentation-status.md).

**Date:** 2026-06-26  
**Scope:** `MrWhoOidc.Auth` multi-tenancy system  
**Status:** Complete

---

## Architecture Overview

### Core Components

#### 1. Configuration & State Management

| File | Purpose |
|---|---|
| `MultiTenancyOptions.cs` | Configuration interface with `Enabled` flag and `DefaultTenantSlug` |
| `MultiTenancyStateProvider.cs` | Runtime state management (volatile boolean) |
| `MultiTenancyStateInitializer.cs` | License-based initialization (reads deployment mode from license) |

#### 2. Tenant Resolution

| File | Purpose |
|---|---|
| `ModeAwareTenantResolver.cs` | Path-based resolution (`/t/{slug}`) |
| `TenantContext.cs` | Runtime context with `TenantId`, `Slug`, `Name`, `IssuerUri` |
| `TenantAccessor.cs` | Scoped service storing current tenant context |
| `DefaultTenantContext.cs` | Resolves default tenant ID from slug |
| `IssuerBuilder.cs` | Constructs issuer URIs based on mode |
| `TenantSlug.cs` | Slug validation (regex, max length 63) |

#### 3. Database Model

| Entity | Key Fields | Notes |
|---|---|---|
| `Tenant` | `Id`, `Slug` (unique), `Name`, `Status`, `IssuerUri`, `SettingsJson`, `MetadataJson`, `BillingPlan`, `LicenseMode` | Core tenant entity |
| `TenantIcon` | `Id`, `TenantId` (FK), `FileName`, `FileData`, `FileSize` | Uploaded logo/icon |
| `UserAccount` | `Id`, `Username` (unique), `Email`, `NormalizedEmail` (unique), `PasswordHash` | **Global** user account (no TenantId) |
| `UserTenantMembership` | `Id`, `UserAccountId` (FK), `TenantId` (FK), `IsTenantAdmin`, `Status` | Junction table |
| `TenantInvitation` | `Id`, `TenantId` (FK), `Email`, `NormalizedEmail`, `TokenHash`, `IsTenantAdmin`, `Status` | Enrollment invitations |
| `TenantDomainClaim` | `Id`, `TenantId` (FK), `Domain`, `NormalizedDomain`, `Status`, `EnrollmentMode` | Domain ownership |
| `User` | `Id`, `TenantId` (FK), `Username`, `Email`, `NormalizedEmail` | **Tenant-scoped** user entity |
| `Realm` | `Id`, `TenantId` (FK), `Name` | Tenant-scoped realm |
| `Role` | `Id`, `RealmId` (FK), `TenantId` (FK), `Name` | Tenant-scoped role |

#### 4. Services

| Service | Purpose |
|---|---|
| `TenantService` | CRUD operations, HybridCache (1hr L2, 15min L1) |
| `TenantEnrollmentService` | Invitation lifecycle (create, accept, revoke) |
| `UserTenantMembershipService` | Membership queries |
| `TenantDomainClaimService` | Domain claims with auto-join modes |
| `TenantsClaimService` | Builds `tenants` JWT claim with role information |

#### 5. Database Isolation

- `AuthDbContext.ApplyTenantWriteGuards()` — Enforces tenant isolation on writes
- Composite unique indexes: `(TenantId, Username)`, `(TenantId, NormalizedEmail)`, `(TenantId, Name)` for Realms/Roles
- Optional `TenantId` (nullable) for platform-wide entities

---

## Identified Weaknesses & Problems

### 🔴 Critical Issues

#### C1. Inefficient Tenant Resolution — Loads ALL Tenants Into Memory

**File:** `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs`

```csharp
var tenants = await _dbContext.Tenants
    .Where(t => t.Status == TenantStatus.Active)
    .ToListAsync(cancellationToken);  // Loads ALL active tenants into memory!

var tenant = tenants.FirstOrDefault(t =>
    t.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
```

**Impact:** O(n) database scan on every tenant resolution. No indexing optimization. Scales poorly with many tenants. The comment in the code even acknowledges "In-memory DB doesn't support ToLower in queries" but doesn't address the production case.

---

#### C2. Inconsistent Caching Strategies

| Service | Cache Type | Expiration |
|---|---|---|
| `TenantService` | HybridCache (Redis + Memory) | 1hr L2 / 15min L1 |
| `TenantResolver` | IMemoryCache | 5min |
| `DefaultTenantContext` | In-memory field | Until dispose |

**Impact:** Different services use different caches with different expiration times, leading to stale data inconsistencies. A tenant update may be visible in one service but not another for up to 1 hour.

---

#### C3. Runtime State vs Configuration Drift

```csharp
// MultiTenancyStateInitializer.cs — updates runtime state
_stateProvider.UpdateState(enabled);

// TenantService.cs — reads from configuration
public bool IsMultiTenantMode => _multiTenancyOptions.Enabled;
```

**Impact:** `MultiTenancyStateProvider` can be updated at runtime, but `TenantService` reads from `IMultiTenancyOptions` which is configuration-based. These can diverge, causing inconsistent behavior.

---

#### C4. No Tenant Name Uniqueness Validation

**File:** `MrWhoOidc.Auth/Services/TenantService.cs`

```csharp
var tenant = new Tenant
{
    Name = name,  // No uniqueness check!
    Slug = slug,  // Slug is unique, but Name is not
    ...
};
```

**Impact:** Multiple tenants can have the same `Name`, causing confusion in UI, reports, and the `tenants` JWT claim.

---

### 🟠 High-Priority Issues

#### H1. Data Duplication: User vs UserAccount

- `UserAccount` is global (no `TenantId`)
- `User` is tenant-scoped (has `TenantId`)
- `TenantService.CreateTenantAsync()` copies data from `UserAccount` to `User`

```csharp
var user = new User
{
    Id = userAccount.Id,  // Same ID
    TenantId = tenant.Id,
    Username = userAccount.Username,
    Email = userAccount.Email,
    ...
};
```

**Impact:** Creates a confusing 1:N relationship where one `UserAccount` can have multiple `User` entities (one per tenant). Data can drift between the two.

---

#### H2. Domain Claims: No Verification

**File:** `MrWhoOidc.Auth/Services/TenantDomainClaimService.cs`

```csharp
var claim = new TenantDomainClaim
{
    Status = TenantDomainClaimStatus.Verified,  // Immediately verified!
    VerifiedAt = now,
    ...
};
```

**Impact:** Domain claims are immediately marked as "Verified" without any DNS verification. The `VerificationToken`, `VerificationDnsName`, and `VerificationDnsValue` fields exist but are never used.

---

#### H3. Hardcoded Public Email Domains

**File:** `MrWhoOidc.Auth/Services/TenantDomainClaimService.cs`

```csharp
private static readonly HashSet<string> PublicEmailDomains = new(StringComparer.OrdinalIgnoreCase)
{
    "aol.com", "gmail.com", "googlemail.com", "gmx.com", "hotmail.com",
    "icloud.com", "live.com", "mac.com", "mail.com", "me.com", "msn.com",
    "outlook.com", "pm.me", "proton.me", "protonmail.com", "yahoo.com", "zoho.com"
};
```

**Impact:** Public email domains are hardcoded and cannot be configured. Organizations cannot use public email domains for tenant enrollment even if needed.

---

#### H4. Tenant Slug Generation is Random

**File:** `MrWhoOidc.Auth/Services/TenantService.cs`

```csharp
var bytes = new byte[8];
using (var rng = RandomNumberGenerator.Create())
{
    rng.GetBytes(bytes);
}
slug = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(bytes);
```

**Impact:** Slugs are random (e.g., `xK9mP2vLqR8`), not user-friendly. Users can't guess or remember tenant URLs.

---

### 🟡 Medium-Priority Issues

#### M1. Tenant Write Guards Don't Validate Navigation Properties

**File:** `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

```csharp
foreach (var entry in ChangeTracker.Entries()
    .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified))
{
    var tenantProperty = entry.Metadata.FindProperty("TenantId");
    // Only checks TenantId property, not navigation properties
}
```

**Impact:** If you update a navigation property (e.g., `user.Tenant = otherTenant`), the write guard won't catch it. Only direct `TenantId` property changes are validated.

---

#### M2. No Rate Limiting on Tenant Creation

**File:** `MrWhoOidc.Auth/Services/TenantService.cs`

`CanProvisionTenantAsync()` only checks if multi-tenancy is enabled. No rate limiting or quota checking beyond `MaxUsers`/`MaxClients`/`MaxIdentityProviders`.

**Impact:** A tenant admin could create unlimited sub-tenants if the architecture supports it.

---

#### M3. Inconsistent Error Handling

- `TenantResolver.ResolveTenantAsync()` returns `null` on failure
- `TenantService.CreateTenantAsync()` throws `InvalidOperationException`
- `TenantEnrollmentService.AcceptInvitationAsync()` returns a result record

**Impact:** Inconsistent error handling makes it hard to write robust code.

---

#### M4. JSON Fields Without Schema Validation

```csharp
[MaxLength(4000)]
public string? SettingsJson { get; set; }  // No schema validation

[MaxLength(2000)]
public string? MetadataJson { get; set; }  // No schema validation
```

**Impact:** JSON fields are stored as strings with no schema validation. Typos or invalid structures won't be caught until runtime.

---

### 🟢 Low-Priority Issues

#### L1. BillingPlan is a String, Not an Enum

```csharp
[MaxLength(100)]
public string? BillingPlan { get; set; }  // "Free", "Starter", "Pro", "Enterprise"
```

No type safety. Typos like "free" vs "Free" won't be caught.

---

#### L2. No Soft Delete Cascade Logic

`Tenant.DeletedAt` field exists but there's no implementation for soft deletes. Tenants are either `Active` or `Deleted` (enum value), but no cascade logic for related entities.

---

#### L3. TenantIcon Delete Behavior is SetNull

```csharp
b.HasOne(x => x.TenantIcon)
    .WithOne(x => x.Tenant)
    .HasForeignKey<Tenant>(x => x.TenantIconId)
    .OnDelete(DeleteBehavior.SetNull);
```

If a tenant is deleted, the icon is orphaned (not cascade deleted). Creates orphaned records.

---

#### L4. No Audit Trail for Tenant Changes

`Tenant` entity has no audit log. `TenantInvitation` has `InvitedByUserId` and `RevokedByUserId`, but `Tenant` creation/modification has no audit trail.

---

## Summary

| Severity | Count | Items |
|---|---|---|
| 🔴 Critical | 4 | C1-C4 |
| 🟠 High | 4 | H1-H4 |
| 🟡 Medium | 4 | M1-M4 |
| 🟢 Low | 4 | L1-L4 |

The multi-tenancy system is **well-structured** with clear separation of concerns, but has several **performance and consistency issues** that should be addressed before production scale.
