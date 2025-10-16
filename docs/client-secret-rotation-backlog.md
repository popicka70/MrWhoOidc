# Client Secret Rotation & Expiry — Production Backlog

**Version**: 1.0  
**Date**: October 16, 2025  
**Status**: Draft  
**Priority**: Medium

---

## Overview

This document captures requirements, design considerations, and implementation tasks for adding **overlapping client secrets** and **secret expiry** support to MrWhoOidc. The goal is to enable zero-downtime client secret rotation with an overlap period where both old and new secrets are valid, similar to the signing key rotation strategy already implemented for Identity Providers.

### Goals

1. **Zero-downtime secret rotation**: Allow clients to rotate secrets without service interruption
2. **Multiple active secrets**: Support 2+ active secrets per client during overlap periods
3. **Expiry enforcement**: Automatically reject expired secrets at authentication time
4. **Admin UX**: Simple UI/API for secret generation, activation, and lifecycle management
5. **Audit trail**: Track who rotated secrets, when, and secret usage patterns
6. **Backward compatibility**: Existing single-secret clients continue working unchanged

### Non-Goals (Future Enhancements)

- Automatic secret rotation (client-side SDK support)
- Secret versioning/tagging beyond primary/secondary distinction
- Integration with external secret managers (Vault, Azure Key Vault, etc.)
- Client-initiated secret rotation via dynamic client registration

---

## Background & References

### Related Work in Codebase

- **Identity Provider Key Rotation**: `docs/key-rotation-playbook.md` and `docs/provider-key-rotation.md` demonstrate overlap strategy for IdP signing keys
- **Client Entity**: `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (lines 760-830) defines current `Client` model with single `ClientSecretHash`
- **ClientStore**: `MrWhoOidc.Auth/Services/ClientStore.cs` handles secret validation via `IPasswordHasher`
- **Token Handler**: `MrWhoOidc.WebAuth/Handlers/TokenHandler.cs` authenticates clients at `/token` endpoint
- **Introspection**: `MrWhoOidc.WebAuth/Handlers/Introspection/ClientAuthenticator.cs` supports mTLS, private_key_jwt, and client_secret

### Industry Standards

- **OAuth 2.0 RFC 6749 § 2.3.1**: Client password authentication
- **OAuth 2.0 Dynamic Client Registration RFC 7591**: Client secret management patterns
- **NIST SP 800-63B**: Password/secret expiry best practices (90-180 day rotation recommended for shared secrets)

### Overlap Strategy Pattern

Borrowed from existing IdP key rotation (see `key-rotation-playbook.md`):

| Phase | Old Secret | New Secret | Description |
|-------|-----------|-----------|-------------|
| **T-7** | Active | Generated (inactive, not valid) | New secret created but not yet usable |
| **T-2** | Active | Activated (valid) | New secret becomes valid; both secrets now accepted |
| **T+0** | Active | Primary (recommended) | Cutover: clients should switch to new secret |
| **T+2** | Grace (valid) | Primary | Old secret still valid for stragglers |
| **T+5** | Expired | Primary | Old secret rejected; cleanup eligible |

**Key insight**: 2-5 day overlap allows gradual client rollover without coordination.

---

## Architecture & Design

### Data Model Changes

#### New Entity: `ClientSecret`

Create separate table for storing multiple secrets per client:

```csharp
public class ClientSecret
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }          // FK to Client.Id
    public Client Client { get; set; } = null!; // Navigation property
    
    [MaxLength(500)]
    public string SecretHash { get; set; } = string.Empty; // Argon2id/BCrypt hash
    
    [MaxLength(100)]
    public string? Description { get; set; }    // User-friendly label ("Production secret Q4 2025")
    
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAtUtc { get; set; }  // null => not yet active
    public DateTime? ExpiresAtUtc { get; set; }    // null => no expiry
    public DateTime? RevokedAtUtc { get; set; }    // null => not revoked
    
    public bool IsPrimary { get; set; } = false;   // Only one primary per client (recommended for new usage)
    
    // Audit fields
    [MaxLength(200)]
    public string? CreatedBy { get; set; }         // Username/subject who created
    [MaxLength(200)]
    public string? ActivatedBy { get; set; }
    [MaxLength(200)]
    public string? RevokedBy { get; set; }
    
    // Usage tracking (optional)
    public DateTime? LastUsedAtUtc { get; set; }
    public long UsageCount { get; set; } = 0;
}
```

#### Migration Path for Existing Clients

##### Option A: Lazy migration (Recommended)

- Keep `Client.ClientSecretHash` for backward compatibility
- When client is loaded, if `ClientSecrets` collection is empty but `ClientSecretHash` is present, treat as single active secret
- Admin UI migrates on first edit: move existing hash to `ClientSecret` row, clear `ClientSecretHash`

##### Option B: Eager migration

- Add migration step that creates `ClientSecret` rows for all existing clients with `ClientSecretHash != null`
- Mark as `IsPrimary = true`, `ActivatedAtUtc = DateTime.UtcNow`, `ExpiresAtUtc = null`
- Clear `Client.ClientSecretHash` column (or deprecate after grace period)

##### Decision

Start with **Option A** for safety; migrate to Option B in v2 after validation period.

#### Client Entity Updates

```csharp
public class Client
{
    // ... existing properties ...
    
    // Keep for backward compatibility (will be null after migration)
    [MaxLength(500)]
    [Obsolete("Use ClientSecrets collection instead")]
    public string? ClientSecretHash { get; set; }
    
    // New navigation property
    public List<ClientSecret> ClientSecrets { get; set; } = new();
}
```

### Service Layer Changes

#### IClientStore Interface Updates

```csharp
public interface IClientStore
{
    // Existing methods (unchanged)
    Task<Client?> FindByClientIdAsync(string clientId, CancellationToken ct = default);
    Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default);
    IQueryable<Client> QueryClients(CancellationToken ct = default);
    Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default);
    
    // New methods for multi-secret support
    Task<ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default);
    Task<List<ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default);
    Task<ClientSecret> CreateSecretAsync(Guid clientRecordId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default);
    Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default);
    Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default);
    Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default);
    Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default); // Increment counter, update last used
}
```

#### ClientStore Implementation Strategy

**ValidateClientSecretAsync** must check:

1. Legacy `Client.ClientSecretHash` if present (backward compatibility)
2. All active `ClientSecret` records where:
   - `ActivatedAtUtc IS NOT NULL`
   - `RevokedAtUtc IS NULL`
   - `ExpiresAtUtc IS NULL OR ExpiresAtUtc > UtcNow`
3. On successful match, optionally call `RecordSecretUsageAsync` (async fire-and-forget or via background queue to avoid perf hit)

**Caching considerations**:

- Invalidate HybridCache entry for client when secrets change
- Consider separate cache key for "active secrets" to avoid full client cache invalidation

### Admin UI/API

#### New Admin Pages

**`/Admin/Clients/{id}/Secrets`** — Secret Management UI

Components:

- **Active Secrets Table**:
  - Columns: Description, Status (Primary/Active/Expired), Created, Activated, Expires, Last Used, Actions
  - Status badges with color coding (green=primary, blue=active, yellow=expiring soon, red=expired)
  - Show partial hash or fingerprint (first 8 chars of Base64-encoded hash) for identification
  - Actions: Set Primary, Revoke, View Audit
- **Add New Secret Form**:
  - Generate button (creates random secure secret, displays once with copy button)
  - Import field (paste existing secret, less common but useful for migration)
  - Description field
  - Expiry date picker (optional, default = 90 days from activation)
  - "Activate immediately" checkbox (default = false for overlap strategy)
- **Rotation Wizard** (optional UX enhancement):
  - Step 1: Generate new secret → displays secret with copy button + safety instructions
  - Step 2: "I've updated my client application" confirmation
  - Step 3: Activate new secret (starts overlap period)
  - Step 4: "Test successful" confirmation
  - Step 5: Revoke old secret (completes rotation)

#### Admin API Endpoints

**`POST /api/admin/clients/{id}/secrets`**

- Body: `{ description?: string, expiresInDays?: number, activateImmediately?: bool }`
- Returns: `{ secretId: guid, secretValue: string (ONLY returned once), expiresAtUtc?: datetime }`
- Requires: `admin:clients:write` permission

**`POST /api/admin/clients/{id}/secrets/{secretId}/activate`**

- Body: `{ }`
- Returns: `{ success: bool, activatedAtUtc: datetime }`

**`POST /api/admin/clients/{id}/secrets/{secretId}/set-primary`**

- Body: `{ }`
- Returns: `{ success: bool }`
- Side effect: Clears `IsPrimary` flag on other secrets for this client

**`DELETE /api/admin/clients/{id}/secrets/{secretId}`** or **`POST .../revoke`**

- Returns: `{ success: bool, revokedAtUtc: datetime }`
- Validation: Prevent revoking last active secret (would lock out client)

**`GET /api/admin/clients/{id}/secrets`**

- Returns: `{ secrets: [ { id, description, status, createdAt, activatedAt, expiresAt, lastUsedAt, usageCount, isPrimary } ] }`
- Excludes: Actual secret values/hashes (never returned after creation)

**`GET /api/admin/clients/{id}/secrets/{secretId}/audit`**

- Returns: Audit log entries for secret lifecycle events

#### Validation Rules

Validation rules to enforce:

- **Maximum active secrets**: 3 per client (prevents abuse; allows primary + 2 rotating)
- **Minimum secret complexity**: 32 characters if user-provided (encourage generated secrets)
- **Expiry bounds**: 1 day minimum, 730 days (2 years) maximum
- **Prevent self-lockout**: Cannot revoke last active secret
- **Primary secret requirement**: Warn if no primary secret set (UX/audit concern, not enforced)

### Security Considerations

#### Secret Generation

- **Entropy**: Use `RandomNumberGenerator.GetBytes(32)` for 256-bit secrets
- **Encoding**: Base64-url-safe encoding for compatibility (44 characters)
- **Display once**: Show plaintext secret only on creation response; never retrieve from DB

#### Secret Storage

- **Hashing**: Continue using `IPasswordHasher` (Argon2id or BCrypt) for all secrets
- **Hash migration**: When migrating legacy `ClientSecretHash` to `ClientSecret`, copy hash directly (no re-hashing)

#### Timing Attacks

- **Constant-time comparison**: `IPasswordHasher.Verify` should already handle this
- **Early exit prevention**: Check all active secrets even after first match (for usage tracking)

#### Audit & Monitoring

- **Secret creation/activation/revocation**: Log to audit table with operator identity
- **Authentication failures**: Log which client attempted auth and whether any secrets existed (without revealing hash info)
- **Expiry warnings**: Emit metrics/alerts 7 days before expiry (`oidc.client_secrets.expiry_warning`)
- **Usage anomalies**: Alert on sudden spike in usage for secondary (non-primary) secrets

### Metrics & Telemetry

#### Metrics (via `OidcMetrics` or new `ClientSecretMetrics`)

Metrics to implement:

- `oidc.client_secrets.active_count` (gauge, by client_id)
- `oidc.client_secrets.authentication_success` (counter, by client_id, secret_id)
- `oidc.client_secrets.authentication_failure` (counter, by client_id, reason: expired|revoked|invalid|missing)
- `oidc.client_secrets.days_until_expiry` (gauge, by client_id, secret_id)
- `oidc.client_secrets.rotation_events` (counter, by action: created|activated|revoked)

#### Structured Logging

```csharp
logger.LogInformation(
    "Client secret authenticated: ClientId={ClientIdHash}, SecretId={SecretId}, IsPrimary={IsPrimary}",
    Bucketization.Bucket(clientId),
    secretId,
    isPrimary
);

logger.LogWarning(
    "Client secret expired: ClientId={ClientIdHash}, SecretId={SecretId}, ExpiredAt={ExpiredAt}",
    Bucketization.Bucket(clientId),
    secretId,
    expiredAtUtc
);
```

---

## Implementation Plan

### Phase 1: Data Model & Persistence (Week 1)

#### Epic 1.1: Database Schema

- [ ] **Task 1.1.1**: Create `ClientSecret` entity in `AuthDbContext.cs`
  - Include all fields from design above
  - Add composite index on `(ClientId, ActivatedAtUtc, RevokedAtUtc, ExpiresAtUtc)` for query perf
  - Add unique index on `(ClientId, IsPrimary)` where `IsPrimary = true AND RevokedAtUtc IS NULL` (ensures only one primary)
- [ ] **Task 1.1.2**: Add navigation property `List<ClientSecret> ClientSecrets` to `Client` entity
- [ ] **Task 1.1.3**: Mark `Client.ClientSecretHash` as `[Obsolete]` with comment
- [ ] **Task 1.1.4**: Generate EF Core migration
  - Command: `dotnet ef migrations add AddClientSecretRotation --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
- [ ] **Task 1.1.5**: Review and test migration (ensure no data loss; legacy column retained)

#### Epic 1.2: Service Layer

- [ ] **Task 1.2.1**: Extend `IClientStore` interface with new methods (see design above)
- [ ] **Task 1.2.2**: Implement new methods in `ClientStore.cs`
  - `GetActiveSecretsAsync`: Query with expiry/revocation filter
  - `CreateSecretAsync`: Hash secret via `IPasswordHasher`, insert record
  - `ActivateSecretAsync`, `RevokeSecretAsync`, `SetPrimarySecretAsync`: Update flags + audit fields
  - `RecordSecretUsageAsync`: Increment counter (consider perf impact; maybe batch or queue)
- [ ] **Task 1.2.3**: Update `ValidateClientSecretAsync` to support multi-secret validation
  - Backward compatibility: Check legacy `ClientSecretHash` first if present
  - Query active `ClientSecret` records, validate each until match
  - On match, call `RecordSecretUsageAsync` (fire-and-forget or background queue)
- [ ] **Task 1.2.4**: Add cache invalidation for secret changes
  - Invalidate client cache entry when secrets are added/activated/revoked

#### Epic 1.3: Unit Tests

- [ ] **Task 1.3.1**: `ClientStoreTests` — multi-secret validation
  - Test: Multiple active secrets, all should validate
  - Test: Expired secret rejected
  - Test: Revoked secret rejected
  - Test: Not-yet-activated secret rejected
  - Test: Legacy `ClientSecretHash` still works (backward compat)
  - Test: Primary secret flag handling
- [ ] **Task 1.3.2**: Integration test: Create client, add 2 secrets, authenticate with both
- [ ] **Task 1.3.3**: Test secret expiry boundary conditions (expires in past, future, null)

---

### Phase 2: Admin UI/API (Week 2)

#### Epic 2.1: Admin API Endpoints

- [ ] **Task 2.1.1**: Create `ClientSecretsAdminController.cs` (or add to existing admin group in `Program.cs`)
- [ ] **Task 2.1.2**: Implement `POST /api/admin/clients/{id}/secrets` (create secret)
  - Generate secure random secret via `RandomNumberGenerator`
  - Return plaintext secret + secretId in response body (ONLY time secret is visible)
  - Validate max active secrets limit (3)
- [ ] **Task 2.1.3**: Implement `POST /api/admin/clients/{id}/secrets/{secretId}/activate`
- [ ] **Task 2.1.4**: Implement `POST /api/admin/clients/{id}/secrets/{secretId}/set-primary`
- [ ] **Task 2.1.5**: Implement `DELETE /api/admin/clients/{id}/secrets/{secretId}` (revoke)
  - Prevent revoking last active secret (return 400 error)
- [ ] **Task 2.1.6**: Implement `GET /api/admin/clients/{id}/secrets` (list secrets)
  - Return summary view without hashes
  - Include computed `status` field: "primary" | "active" | "expired" | "revoked" | "inactive"
- [ ] **Task 2.1.7**: Add authorization checks (require `admin:clients:write` scope/role)
- [ ] **Task 2.1.8**: Add audit logging for all secret lifecycle events

#### Epic 2.2: Admin UI Pages

- [ ] **Task 2.2.1**: Create Razor page `/Admin/Clients/{id}/Secrets.cshtml`
  - Display active secrets in table with status badges
  - "Add Secret" button → modal/form
  - Actions per secret: Activate, Set Primary, Revoke, View Audit
- [ ] **Task 2.2.2**: Create secret generation modal
  - Generate button → call API → display secret with copy-to-clipboard button
  - **CRITICAL UX**: Show warning "Save this secret now. You won't see it again."
  - Display QR code for mobile copying (optional enhancement)
- [ ] **Task 2.2.3**: Add "Secrets" tab/link to existing Client Edit page navigation
- [ ] **Task 2.2.4**: Display expiry warnings in UI (badge/icon if secret expires within 7 days)
- [ ] **Task 2.2.5**: Add confirmation dialogs for revocation
  - Extra confirmation if revoking primary secret
  - Prevent revoking last secret (disable button + tooltip)

#### Epic 2.3: Admin API Tests

- [ ] **Task 2.3.1**: API test: Create secret, verify returned value authenticates
- [ ] **Task 2.3.2**: API test: Activate secret, verify status changes
- [ ] **Task 2.3.3**: API test: Revoke secret, verify authentication fails
- [ ] **Task 2.3.4**: API test: Prevent exceeding max secrets limit
- [ ] **Task 2.3.5**: API test: Prevent revoking last active secret
- [ ] **Task 2.3.6**: API test: Set primary secret, verify only one primary per client
- [ ] **Task 2.3.7**: API test: Authorization checks (require admin role)

---

### Phase 3: Expiry & Monitoring (Week 3)

#### Epic 3.1: Expiry Enforcement

- [ ] **Task 3.1.1**: Update `ValidateClientSecretAsync` to check `ExpiresAtUtc`
  - If `ExpiresAtUtc <= DateTime.UtcNow`, reject secret with specific error code
- [ ] **Task 3.1.2**: Log expiry-related authentication failures distinctly
  - `logger.LogWarning("Client secret expired: ...")`
  - Include `SecretId` (bucketed) and `ExpiredAt` timestamp
- [ ] **Task 3.1.3**: Add integration test: Secret expires after set time, auth fails

#### Epic 3.2: Background Expiry Monitoring

- [ ] **Task 3.2.1**: Create `ClientSecretExpiryMonitor` background service
  - Run daily (or hourly in prod)
  - Query secrets expiring within 7 days: `WHERE ExpiresAtUtc BETWEEN UtcNow AND UtcNow + 7 days`
  - Emit metrics: `oidc.client_secrets.expiry_warning` with days_until_expiry
- [ ] **Task 3.2.2**: Add health check endpoint: `/health/client-secrets`
  - Returns unhealthy if any client has ALL secrets expired (critical)
  - Returns degraded if any client has secrets expiring within 3 days
- [ ] **Task 3.2.3**: Add admin notification mechanism (email/webhook)
  - Notify tenant admins when their client secrets are expiring
  - Include link to Admin UI secret management page
  - **TODO**: Integration with existing notification system (if any) or new implementation

#### Epic 3.3: Metrics & Telemetry

- [ ] **Task 3.3.1**: Add OpenTelemetry metrics (see Observability section above)
  - Create `ClientSecretMetrics` class similar to `OidcMetrics`
  - Emit counters/gauges for authentication success/failure, active secret count, expiry
- [ ] **Task 3.3.2**: Add structured logging for secret usage
  - On successful auth, log which `SecretId` was used and whether it's primary
- [ ] **Task 3.3.3**: Document metrics in `docs/telemetry-taxonomy.md` (if exists)
- [ ] **Task 3.3.4**: Add Grafana dashboard queries (optional, if using Prometheus/Grafana)
  - Panel: Active secrets per client
  - Panel: Secret expiry timeline
  - Alert: Secret expires within 7 days

---

### Phase 4: Migration & Documentation (Week 4)

#### Epic 4.1: Legacy Secret Migration

- [ ] **Task 4.1.1**: Create `MigrateClientSecrets` admin API endpoint (optional)
  - Manual trigger: `POST /api/admin/clients/{id}/migrate-secrets`
  - Moves `ClientSecretHash` to new `ClientSecret` record
  - Marks as `IsPrimary = true`, `ActivatedAtUtc = DateTime.UtcNow`
  - Sets `Description = "Migrated from legacy secret"`
  - Clears `Client.ClientSecretHash` column
- [ ] **Task 4.1.2**: Create automated migration script (EF migration or background job)
  - Query all clients where `ClientSecretHash IS NOT NULL AND ClientSecrets.Count == 0`
  - Create `ClientSecret` records for each
  - **Decision point**: Run automatically on startup (once) or require manual admin action?
- [ ] **Task 4.1.3**: Add deprecation warning to Admin UI for clients still using legacy secret
  - Badge/banner: "This client uses a legacy secret. Migrate to the new secret management system."
  - Link to migration endpoint/wizard

#### Epic 4.2: Documentation

- [ ] **Task 4.2.1**: Create `docs/client-secret-rotation-guide.md`
  - User-facing guide: How to rotate secrets for your client application
  - Step-by-step: Generate → Update app config → Activate → Test → Revoke old
  - Include code examples for common client libraries
- [ ] **Task 4.2.2**: Create `docs/client-secret-rotation-playbook.md` (admin-facing)
  - Operational playbook similar to `key-rotation-playbook.md`
  - Recommended rotation schedule (e.g., 90-day cycle)
  - Runbook for emergency secret rotation (compromise scenario)
  - Monitoring and alerting setup
- [ ] **Task 4.2.3**: Update `docs/admin-guide.md` with secret management section
  - Link to new Secrets management page
  - Explain primary vs. active vs. expired secret states
- [ ] **Task 4.2.4**: Update `copilot-instructions.md` with secret rotation conventions
  - Add to "Security conventions" section
  - Document multi-secret validation flow
- [ ] **Task 4.2.5**: Add section to `docs/developer-guide.md`
  - Explain `IClientStore` new methods
  - Code example: How to validate secrets in custom endpoint
- [ ] **Task 4.2.6**: Update `README.md` with feature mention (if appropriate)

#### Epic 4.3: Test Coverage

- [ ] **Task 4.3.1**: Add E2E test: Full rotation workflow via Admin API
  - Create client → Generate secret → Authenticate → Generate 2nd secret → Authenticate with both → Revoke 1st → Authenticate only with 2nd
- [ ] **Task 4.3.2**: Add E2E test: Expiry workflow
  - Create secret with 1-second expiry → Wait → Verify auth fails
- [ ] **Task 4.3.3**: Add E2E test: Legacy secret backward compatibility
  - Load existing client with `ClientSecretHash` → Verify auth works → Migrate → Verify still works
- [ ] **Task 4.3.4**: Performance test: Auth latency with 3 active secrets vs. 1 secret
  - Ensure multi-secret validation doesn't significantly degrade performance
- [ ] **Task 4.3.5**: Update `docs/test-coverage-backlog.md` with new test cases

---

## Testing Strategy

### Unit Tests

Focus areas:

- `ClientStore.ValidateClientSecretAsync` with multiple secrets (active, expired, revoked, inactive)
- Secret lifecycle methods (create, activate, revoke, set primary)
- Backward compatibility with legacy `ClientSecretHash`
- Expiry boundary conditions
- Primary secret uniqueness constraint

### Integration Tests

- Multi-tenant secret isolation (ensure Tenant A cannot use Tenant B's secrets)
- Cache invalidation on secret changes
- Admin API authorization (RBAC enforcement)
- Database constraints (unique primary index, FK relationships)

### E2E Tests

- Full rotation workflow via Admin UI
- Secret expiry enforcement in token endpoint authentication
- Migration from legacy to new secret model
- Client authentication with multiple active secrets

### Performance Tests

- Auth latency with 1 vs. 3 active secrets (expect <5ms difference)
- Concurrent authentication requests with same client (cache effectiveness)

### Security Tests

- **Secret exposure**: Verify plaintext secret never stored in DB
- **Timing attacks**: Verify constant-time comparison in validation
- **Enumeration**: Verify error messages don't reveal secret existence/count
- **Authorization**: Verify tenant isolation (admin from Tenant A cannot manage Tenant B secrets)

---

## Rollout Plan

### Stage 1: Feature Flag (Opt-In)

Add feature flag to `appsettings.json`:

```json
{
  "Features": {
    "ClientSecretRotation": {
      "Enabled": false,  // Default off for initial release
      "MaxActiveSecrets": 3,
      "DefaultExpiryDays": 90,
      "EnforceExpiry": false  // Allow testing without hard enforcement first
    }
  }
}
```

Rollout steps:

1. Deploy with `Enabled = false` (code present but inactive)
2. Enable for internal/test tenants only
3. Monitor metrics/logs for 1 week
4. Enable globally, still with `EnforceExpiry = false`
5. Enable `EnforceExpiry = true` after migration period

### Stage 2: Gradual Migration

- Week 1-2: Announce feature to tenant admins via in-app banner + email
- Week 3-4: Encourage migration via Admin UI banner for legacy clients
- Week 5: Run automated migration for remaining clients (with notification)
- Week 6+: Mark `Client.ClientSecretHash` column as deprecated (keep for rollback safety)

### Stage 3: Deprecation (6 months post-launch)

- Remove backward compatibility code for legacy `ClientSecretHash`
- Drop `Client.ClientSecretHash` column in migration
- Simplify `ValidateClientSecretAsync` logic

---

## Security Audit Checklist

Before production release:

- [ ] **Secret storage**: All secrets hashed with Argon2id/BCrypt (min 10 rounds)
- [ ] **Secret generation**: 256-bit entropy via `RandomNumberGenerator`
- [ ] **Secret exposure**: Plaintext secret only returned once on creation; never logged
- [ ] **Timing attacks**: `IPasswordHasher.Verify` uses constant-time comparison
- [ ] **Audit logging**: All secret lifecycle events logged with operator identity
- [ ] **Authorization**: Admin endpoints require appropriate RBAC roles/scopes
- [ ] **Tenant isolation**: Secrets scoped to tenant; cross-tenant access prevented
- [ ] **Enumeration prevention**: Error messages generic (don't reveal secret count/status)
- [ ] **Expiry enforcement**: Expired secrets rejected at validation time (no grace period by default)
- [ ] **Revocation enforcement**: Revoked secrets immediately invalidated (cache cleared)
- [ ] **Self-lockout prevention**: Cannot revoke last active secret via UI/API
- [ ] **PII handling**: Secret IDs bucketed in logs; no hash exposure

---

## Open Questions & Decisions Needed

### Q1: Usage tracking performance impact

**Question**: Should we track `LastUsedAtUtc` and `UsageCount` on every authentication?

**Options**:

- A) Update synchronously (simple, but adds DB write to every auth)
- B) Queue updates, process in background (complex, but no perf impact)
- C) Track in metrics/logs only, no DB persistence (simplest, loses per-secret granularity)

**Recommendation**: Start with **Option C** (metrics only). If usage tracking proves valuable, add **Option B** in future release.

---

### Q2: Automatic migration vs. manual

**Question**: Should we auto-migrate legacy `ClientSecretHash` to new model?

**Options**:

- A) Auto-migrate on app startup (run once via EF migration or background job)
- B) Lazy migration: Convert on first admin edit of client
- C) Manual admin action required (safer, but requires coordination)

**Recommendation**: **Option B** (lazy migration) for initial release. Provides backward compat without forced migration. Add **Option A** after validation period if desired.

---

### Q3: Maximum active secrets limit

**Question**: Should we limit how many active secrets a client can have?

**Options**:

- A) No limit (flexible, but risk of secret sprawl)
- B) Limit to 3 active secrets (covers primary + 2 rotating)
- C) Configurable per tenant (complex)

**Recommendation**: **Option B** (hard limit of 3). This supports overlap rotation without allowing abuse. Can be made configurable later if needed.

---

### Q4: Expiry default and enforcement

**Question**: Should secret expiry be mandatory? What's the default?

**Options**:

- A) Mandatory 90-day expiry (most secure, but may disrupt existing workflows)
- B) Optional expiry, default = null (backward compatible)
- C) Optional expiry, default = 90 days (nudge toward best practice)

**Recommendation**: **Option C** (optional with 90-day default). Allow admins to set longer expiry or none for testing, but encourage rotation via default.

---

### Q5: Primary secret behavior

**Question**: How should "primary" secret be used?

**Options**:

- A) Purely advisory (just a UX label, no enforcement)
- B) Enforce: Only primary secret allowed after overlap period expires
- C) Prefer primary in discovery/metadata (if we expose secret hints in OIDC metadata)

**Recommendation**: **Option A** (advisory only). Primary flag helps admins understand which secret is "current" but doesn't restrict usage. Enforcement can be added later if needed.

---

## Success Criteria

### Functional Requirements

- [ ] Clients can have 2+ active secrets simultaneously
- [ ] Authentication succeeds with any active, non-expired, non-revoked secret
- [ ] Expired secrets are rejected with appropriate error
- [ ] Admin UI allows creating, activating, revoking secrets
- [ ] Admin UI shows secret status (primary/active/expired/revoked)
- [ ] Backward compatibility: Existing single-secret clients continue working
- [ ] Migration path from legacy `ClientSecretHash` to new model

### Non-Functional Requirements

- [ ] Auth latency increase <5ms when checking 3 secrets vs. 1 secret
- [ ] Secret creation response time <200ms (including hash generation)
- [ ] Zero service disruption during secret rotation
- [ ] Audit trail captures all secret lifecycle events
- [ ] Metrics/logs enable debugging secret-related auth failures
- [ ] Documentation covers operator and developer workflows

### Observability

- [ ] Metrics emitted for secret count, auth success/failure, expiry warnings
- [ ] Logs capture secret lifecycle events (creation, activation, revocation)
- [ ] Health check reflects secret expiry status
- [ ] Alerts configured for secrets expiring within 7 days

---

## Related Work & Future Enhancements

### Short-Term (Next 6 Months)

- **Client-initiated rotation**: Allow clients to rotate their own secrets via dynamic client registration API
- **Secret templates**: Pre-configure secret complexity requirements per tenant
- **Notification system**: Integrate with email/webhook system for expiry alerts
- **Secret import/export**: Allow bulk secret management via CSV/JSON

### Long-Term (12+ Months)

- **External secret manager integration**: Support Azure Key Vault, HashiCorp Vault for secret storage
- **Hardware security module (HSM)**: Store secrets in HSM for regulated environments
- **Client secret encryption**: Encrypt hashes at rest (currently rely on DB encryption)
- **Secret versioning**: Tag secrets with version numbers/labels beyond primary/secondary
- **Automatic rotation**: SDK-driven automatic rotation with callback verification
- **Secret policy inheritance**: Define tenant-level secret policies (expiry, complexity, max active)

### Related Backlog Items

- `backchannel-logout-backlog.md`: Consider secret rotation impact on BCL token signing
- `key-rotation-playbook.md`: Align secret rotation UX/terminology with signing key rotation
- `multitenancy-security-audit-october-2025.md`: Ensure secret isolation in multi-tenant scenarios
- `test-coverage-backlog.md`: Add secret rotation test cases to Story 2.6 (ClientStore Tests)

---

## References

### RFCs & Standards

- [RFC 6749 - OAuth 2.0 (§ 2.3.1 Client Password)](https://datatracker.ietf.org/doc/html/rfc6749#section-2.3.1)
- [RFC 7591 - OAuth 2.0 Dynamic Client Registration](https://datatracker.ietf.org/doc/html/rfc7591)
- [RFC 7592 - OAuth 2.0 Dynamic Client Registration Management](https://datatracker.ietf.org/doc/html/rfc7592)

### Industry Best Practices

- [NIST SP 800-63B - Digital Identity Guidelines (Authentication)](https://pages.nist.gov/800-63-3/sp800-63b.html)
- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)

### Internal Documentation

- `docs/key-rotation-playbook.md` — Overlap rotation strategy for signing keys
- `docs/admin-guide.md` — Admin UI usage patterns
- `docs/developer-guide.md` — Service layer architecture
- `docs/security/` — Security architecture and audit findings
- `copilot-instructions.md` — Codebase conventions and patterns

---

## Appendix A: Database Schema Reference

### Migration Command

```powershell
dotnet ef migrations add AddClientSecretRotation `
  --project MrWhoOidc.Auth `
  --startup-project MrWhoOidc.WebAuth `
  --output-dir Persistence/Migrations
```

### Expected Schema (Simplified SQL)

```sql
CREATE TABLE ClientSecrets (
    Id UUID PRIMARY KEY,
    ClientId UUID NOT NULL REFERENCES Clients(Id) ON DELETE CASCADE,
    SecretHash VARCHAR(500) NOT NULL,
    Description VARCHAR(100),
    CreatedAtUtc TIMESTAMP NOT NULL,
    ActivatedAtUtc TIMESTAMP,
    ExpiresAtUtc TIMESTAMP,
    RevokedAtUtc TIMESTAMP,
    IsPrimary BOOLEAN NOT NULL DEFAULT FALSE,
    CreatedBy VARCHAR(200),
    ActivatedBy VARCHAR(200),
    RevokedBy VARCHAR(200),
    LastUsedAtUtc TIMESTAMP,
    UsageCount BIGINT NOT NULL DEFAULT 0
);

-- Performance index for validation queries
CREATE INDEX IX_ClientSecrets_Active 
    ON ClientSecrets(ClientId, ActivatedAtUtc, RevokedAtUtc, ExpiresAtUtc);

-- Uniqueness: Only one primary secret per client (if not revoked)
CREATE UNIQUE INDEX IX_ClientSecrets_PrimaryPerClient 
    ON ClientSecrets(ClientId, IsPrimary) 
    WHERE IsPrimary = TRUE AND RevokedAtUtc IS NULL;
```

---

## Appendix B: API Examples

### Create Secret (Admin)

**Request:**

```http
POST /api/admin/clients/550e8400-e29b-41d4-a716-446655440000/secrets
Content-Type: application/json
Authorization: Bearer {admin_token}

{
  "description": "Q4 2025 Production Secret",
  "expiresInDays": 90,
  "activateImmediately": false
}
```

**Response:**

```json
{
  "secretId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "secretValue": "MrWho_K8sD3f9JpQz7Yh2NmXvL4cR6tU0wE5gA1bN9iO8",
  "expiresAtUtc": "2026-01-14T12:00:00Z",
  "warning": "Save this secret now. It will not be shown again."
}
```

### List Secrets (Admin)

**Request:**

```http
GET /api/admin/clients/550e8400-e29b-41d4-a716-446655440000/secrets
Authorization: Bearer {admin_token}
```

**Response:**

```json
{
  "clientId": "550e8400-e29b-41d4-a716-446655440000",
  "secrets": [
    {
      "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
      "description": "Q4 2025 Production Secret",
      "status": "primary",
      "isPrimary": true,
      "createdAtUtc": "2025-10-16T12:00:00Z",
      "activatedAtUtc": "2025-10-16T12:05:00Z",
      "expiresAtUtc": "2026-01-14T12:00:00Z",
      "lastUsedAtUtc": "2025-10-16T14:30:00Z",
      "usageCount": 1523
    },
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "description": "Q3 2025 Legacy Secret",
      "status": "active",
      "isPrimary": false,
      "createdAtUtc": "2025-07-01T09:00:00Z",
      "activatedAtUtc": "2025-07-01T09:05:00Z",
      "expiresAtUtc": "2025-10-20T09:00:00Z",
      "lastUsedAtUtc": "2025-10-15T08:45:00Z",
      "usageCount": 4521,
      "warningDaysUntilExpiry": 4
    }
  ]
}
```

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-10-16 | AI Assistant | Initial draft based on IdP key rotation pattern |

---

**Status Summary:**

- ✅ Architecture designed (overlap strategy, multi-secret support)
- ✅ Data model defined (`ClientSecret` entity, migration path)
- ✅ Service layer interface defined (`IClientStore` extensions)
- ✅ Admin UI/API requirements captured
- ✅ Implementation plan (4-week phased approach)
- ⏳ Implementation: Not started
- ⏳ Testing: Not started
- ⏳ Documentation: Backlog only

**Next Steps:**

1. Review and approve backlog with team
2. Prioritize against other backlog items (BCL, IdP chaining, QR login, etc.)
3. Assign to sprint and begin Phase 1 (Data Model & Persistence)
4. Schedule security review before production rollout
