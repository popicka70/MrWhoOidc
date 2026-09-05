# Multi-Tenancy Fix Implementation Plan

> **Historical remediation plan; not an execution checklist.** The original sequence and unchecked items do not establish current implementation status. Reproduce the corresponding finding and inspect current code before applying a change. See [documentation status](documentation-status.md) for the active verification queue.

**Based on:** `docs/multi-tenancy-assessment.md`  
**Date:** 2026-06-26

---

## Implementation Strategy

Each fix is designed to be **independent** and **testable**. Sub-agents will work on individual issues in parallel where possible.

---

## Phase 1: Critical Fixes (C1-C4)

### C1: Fix Tenant Resolution Performance

**Problem:** `ModeAwareTenantResolver` loads ALL active tenants into memory for case-insensitive slug lookup.

**Fix:**
1. Add a case-insensitive index on `Tenant.Slug` in the migration
2. Use a SQL-based case-insensitive query instead of loading all tenants
3. For PostgreSQL: use `ILIKE` or `LOWER()` in the query
4. Keep the in-memory cache but fix the database query

**Files to modify:**
- `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs`
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (add index)
- New migration file

**Testing:**
- Unit test: verify slug lookup works with mixed case
- Integration test: verify performance with many tenants

---

### C2: Align Caching Strategies

**Problem:** Different services use different caches with different expiration times.

**Fix:**
1. Create a shared `TenantCacheOptions` class with consistent expiration times
2. Update `TenantService` and `TenantResolver` to use the same cache configuration
3. Add cache invalidation events when tenants are created/updated/deleted

**Files to modify:**
- `MrWhoOidc.Auth/Options/TenantCacheOptions.cs` (new file)
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs`
- `MrWhoOidc.Auth/DependencyInjection.cs` (register shared options)

**Testing:**
- Unit test: verify cache expiration times are consistent
- Integration test: verify cache invalidation works

---

### C3: Fix Runtime State vs Configuration Drift

**Problem:** `MultiTenancyStateProvider` can be updated at runtime, but `TenantService` reads from `IMultiTenancyOptions` (configuration).

**Fix:**
1. Make `TenantService` use `IMultiTenancyStateProvider` instead of `IMultiTenancyOptions`
2. Ensure `MultiTenancyStateProvider` is the single source of truth
3. Add validation to prevent drift

**Files to modify:**
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/MultiTenancy/MultiTenancyStateProvider.cs`
- `MrWhoOidc.Auth/DependencyInjection.cs`

**Testing:**
- Unit test: verify `TenantService.IsMultiTenantMode` reflects runtime state
- Integration test: verify state changes are reflected immediately

---

### C4: Add Tenant Name Uniqueness Validation

**Problem:** Multiple tenants can have the same `Name`.

**Fix:**
1. Add a unique index on `Tenant.Name` (with filter for non-null)
2. Add validation in `TenantService.CreateTenantAsync()` to check for duplicate names
3. Update migration to add the index

**Files to modify:**
- `MrWhoOidc.Auth/Persistence/Tenant.cs` (add validation attribute)
- `MrWhoOidc.Auth/Services/TenantService.cs` (add duplicate check)
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (add index)
- New migration file

**Testing:**
- Unit test: verify duplicate name is rejected
- Integration test: verify unique index prevents duplicates

---

## Phase 2: High-Priority Fixes (H1-H4)

### H1: Clarify User vs UserAccount Relationship

**Problem:** Confusing 1:N relationship between `UserAccount` (global) and `User` (tenant-scoped).

**Fix:**
1. Add clear documentation comments explaining the relationship
2. Add a `UserAccountService` to manage the synchronization
3. Add validation to prevent data drift

**Files to modify:**
- `MrWhoOidc.Auth/Services/UserAccountService.cs` (new file)
- `MrWhoOidc.Auth/Services/TenantService.cs` (use new service)
- `MrWhoOidc.Auth/Persistence/UserAccount.cs` (add documentation)
- `MrWhoOidc.Auth/Persistence/User.cs` (add documentation)

**Testing:**
- Unit test: verify synchronization works
- Integration test: verify data consistency

---

### H2: Implement Domain Verification

**Problem:** Domain claims are immediately marked as "Verified" without DNS verification.

**Fix:**
1. Change default status to `PendingVerification`
2. Add a verification email with DNS record instructions
3. Add a background job to check DNS records
4. Add a manual verification endpoint for testing

**Files to modify:**
- `MrWhoOidc.Auth/Persistence/TenantDomainClaim.cs` (add status)
- `MrWhoOidc.Auth/Services/TenantDomainClaimService.cs` (implement verification)
- `MrWhoOidc.Auth/Services/DnsVerificationService.cs` (new file)
- `MrWhoOidc.Auth/BackgroundServices/DnsVerificationBackgroundService.cs` (new file)

**Testing:**
- Unit test: verify verification flow
- Integration test: verify DNS check works

---

### H3: Make Public Email Domains Configurable

**Problem:** Public email domains are hardcoded.

**Fix:**
1. Move public email domains to `appsettings.json` or database
2. Add configuration options for custom public domains
3. Add validation to prevent abuse

**Files to modify:**
- `MrWhoOidc.Auth/Options/PublicEmailDomainOptions.cs` (new file)
- `MrWhoOidc.Auth/Services/TenantDomainClaimService.cs` (use configuration)
- `MrWhoOidc.Auth/DependencyInjection.cs` (register options)

**Testing:**
- Unit test: verify configuration is used
- Integration test: verify custom domains work

---

### H4: Allow Custom Tenant Slugs

**Problem:** Slugs are random and not user-friendly.

**Fix:**
1. Add a `Slug` parameter to `TenantService.CreateTenantAsync()`
2. Validate the slug format (already done by `TenantSlug.IsValid()`)
3. Generate a fallback slug if not provided

**Files to modify:**
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/Services/ITenantService.cs`
- `MrWhoOidc.Auth/Persistence/Tenant.cs` (add validation)

**Testing:**
- Unit test: verify custom slug is used
- Integration test: verify fallback works

---

## Phase 3: Medium-Priority Fixes (M1-M4)

### M1: Enhance Tenant Write Guards

**Problem:** Write guards don't validate navigation properties.

**Fix:**
1. Add navigation property validation to `ApplyTenantWriteGuards()`
2. Check related entities for tenant ID consistency

**Files to modify:**
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

**Testing:**
- Unit test: verify navigation property validation

---

### M2: Add Rate Limiting on Tenant Creation

**Problem:** No rate limiting on tenant creation.

**Fix:**
1. Add a rate limiter to `TenantService.CreateTenantAsync()`
2. Configure rate limit in options

**Files to modify:**
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/Options/TenantCreationOptions.cs` (new file)

**Testing:**
- Unit test: verify rate limiting works

---

### M3: Standardize Error Handling

**Problem:** Inconsistent error handling (null vs exceptions vs result records).

**Fix:**
1. Define a `TenantOperationException` for tenant-related errors
2. Update methods to use consistent error handling
3. Add error code constants

**Files to modify:**
- `MrWhoOidc.Auth/Exceptions/TenantOperationException.cs` (new file)
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/MultiTenancy/TenantResolver.cs`
- `MrWhoOidc.Auth/Services/TenantEnrollmentService.cs`

**Testing:**
- Unit test: verify error handling is consistent

---

### M4: Add JSON Schema Validation

**Problem:** JSON fields have no schema validation.

**Fix:**
1. Add JSON schema validation in `TenantService`
2. Add validation attributes to entity properties

**Files to modify:**
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/Validation/TenantSettingsValidator.cs` (new file)

**Testing:**
- Unit test: verify schema validation works

---

## Phase 4: Low-Priority Fixes (L1-L4)

### L1: Convert BillingPlan to Enum

**Problem:** `BillingPlan` is a string, not an enum.

**Fix:**
1. Create `BillingPlan` enum
2. Update `Tenant` entity
3. Add migration to convert existing data

**Files to modify:**
- `MrWhoOidc.Auth/Persistence/BillingPlan.cs` (new file)
- `MrWhoOidc.Auth/Persistence/Tenant.cs`
- Migration file

**Testing:**
- Unit test: verify enum values
- Integration test: verify migration works

---

### L2: Implement Soft Delete Cascade Logic

**Problem:** No cascade logic for tenant soft deletes.

**Fix:**
1. Add cascade logic in `TenantService.DeleteTenantAsync()`
2. Archive or delete related entities

**Files to modify:**
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

**Testing:**
- Integration test: verify cascade delete works

---

### L3: Fix TenantIcon Delete Behavior

**Problem:** TenantIcon is orphaned when tenant is deleted.

**Fix:**
1. Change delete behavior to `Cascade`
2. Add migration to update foreign key

**Files to modify:**
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`
- Migration file

**Testing:**
- Integration test: verify cascade delete works

---

### L4: Add Audit Trail for Tenant Changes

**Problem:** No audit trail for tenant creation/modification.

**Fix:**
1. Create `TenantAuditLog` entity
2. Add audit logging to `TenantService`
3. Add audit log queries

**Files to modify:**
- `MrWhoOidc.Auth/Persistence/TenantAuditLog.cs` (new file)
- `MrWhoOidc.Auth/Services/TenantService.cs`
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

**Testing:**
- Integration test: verify audit log is created

---

## Execution Order

1. **Phase 1** (C1-C4): Critical fixes — do first
2. **Phase 2** (H1-H4): High-priority fixes — do second
3. **Phase 3** (M1-M4): Medium-priority fixes — do third
4. **Phase 4** (L1-L4): Low-priority fixes — do last

Each phase can be worked on in parallel by different sub-agents.

---

## Testing Strategy

- **Unit tests:** Test individual components in isolation
- **Integration tests:** Test interactions between components
- **E2E tests:** Test the full system (existing suite)
- **Migration tests:** Test database migrations

---

## Rollback Plan

Each fix is designed to be independent and testable. If a fix causes issues:
1. Revert the code changes
2. Roll back the migration (if any)
3. Verify tests pass

---

## Success Criteria

- All critical and high-priority issues are fixed
- All tests pass (unit, integration, E2E)
- No breaking changes to existing functionality
- Performance improvements are measurable
