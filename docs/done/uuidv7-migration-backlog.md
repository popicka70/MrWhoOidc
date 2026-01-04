# UUIDv7 Migration — Production Backlog

**Version**: 1.0  
**Date**: October 17, 2025  
**Status**: Draft  
**Priority**: Medium

---

## Overview

This document captures requirements, design considerations, and implementation tasks for migrating from standard GUIDs (UUIDv4) to **UUIDv7** (time-ordered UUIDs) for all primary keys in MrWhoOidc. UUIDv7 provides better database performance through improved B-tree index locality while maintaining compatibility with existing GUID/UUID infrastructure.

### Goals

1. **Improved database performance**: Leverage time-ordered UUIDs for better index locality and reduced page splits
2. **Maintain compatibility**: Keep existing UUID data type in PostgreSQL; no schema type changes required
3. **Seamless migration**: Existing records remain valid; only new records use UUIDv7 generation
4. **Consistent generation**: Centralized UUIDv7 generation logic across all entities
5. **Preserve semantics**: IDs remain globally unique, non-sequential, and unpredictable at the bit level
6. **Zero breaking changes**: External APIs continue to accept/return standard UUID strings

### Non-Goals (Future Enhancements)

- Rewriting existing UUIDs to UUIDv7 format (existing data remains as-is)
- Custom UUID encoding formats (stick to RFC 9562 standard)
- Exposing embedded timestamps from UUIDv7 in APIs
- Migrating to other time-ordered ID schemes (ULID, Snowflake, etc.)

---

## Background & Context

### Current State

**Database provider**: PostgreSQL via Npgsql.EntityFrameworkCore.PostgreSQL 9.0.4  
**ORM**: Entity Framework Core 9.0.9  
**Current PK generation**: `Guid.NewGuid()` (UUIDv4) assigned at entity construction

### Affected Entities (as of 2025-10-17)

All entities in `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` use `Guid Id` with default value `Guid.NewGuid()`:

#### Core Identity & Tenancy
- `Tenant` (multi-tenancy root)
- `User` (authentication principals)
- `Realm` (logical user/client grouping)
- `Role` (authorization roles per realm)
- `Scope` (OAuth/OIDC scopes)

#### Clients & Configuration
- `Client` (OAuth/OIDC clients)
- `ClientSecret` (rotatable client credentials)
- `ClientScope` (client-scope assignments)
- `ClientIdentityProvider` (IdP chaining config)
- `ClientJwksHistory` (JWKS audit trail)

#### User Management
- `UserAlternativeEmail` (alternate emails per user)
- `UserClientAssignment` (per-client user access)
- `UserRoleAssignment` (legacy role assignments)
- `UserRealmRoleAssignment` (realm-scoped roles)
- `UserClientRoleAssignment` (client-scoped roles)

#### Protocol & Session State
- `AuthorizationCode` (OIDC authorization codes)
- `Token` (access/refresh tokens)
- `Consent` (user consent records)
- `PushedAuthorizationRequest` (PAR state)
- `QrLoginSession` (QR code login flows)
- `LogoutRedirectReference` (post-logout redirect state)

#### Identity Federation
- `IdentityProvider` (upstream IdP config)
- `IdentityProviderClaimMapping` (claim transformations)
- `IdentityProviderKey` (IdP signing keys)
- `ExternalIdentity` (issuer+sub linkage)

#### Security & Audit
- `SigningKey` (OP signing keys)
- `RevocationAudit` (token revocation audit)
- `BackchannelLogoutNotification` (BCL outbox)
- `ImpersonationAuditLog` (admin impersonation tracking)

#### Other
- `Registration` (user self-registration)
- `DataProtectionKey` (ASP.NET Data Protection)

**Total entities**: ~30 entity types across all bounded contexts

### Why UUIDv7?

**Problem with UUIDv4**:
- Random distribution causes poor B-tree index locality
- Insert hotspots at random leaf pages → frequent page splits
- Reduced cache efficiency due to scattered writes
- Suboptimal for time-series queries (no implicit ordering)

**Benefits of UUIDv7** (RFC 9562):
- **Time-ordered**: 48-bit millisecond timestamp prefix ensures monotonic ordering
- **Index-friendly**: Sequential writes reduce page splits by ~80-90%
- **Cache-efficient**: Hot index pages remain in buffer pool
- **Standard**: RFC 9562 ratified; native support in PostgreSQL 17+ (via `uuid_extract_time()`)
- **Compatible**: Still 128-bit UUIDs; works with existing `uuid` columns
- **Non-predictable**: 74 bits of randomness maintain security properties

**PostgreSQL specifics**:
- Native `uuid` type remains unchanged
- Npgsql maps `Guid` ↔ PostgreSQL `uuid` seamlessly
- UUIDv7 format: `TTTTTTTT-TTTT-7xxx-yxxx-xxxxxxxxxxxx` (T=timestamp, x/y=random+version bits)
- Index performance improvements observed in benchmarks (see references below)

---

## Technical Design

### 1. UUIDv7 Generation Library

**Decision**: Use existing, well-tested NuGet package rather than implementing RFC 9562 ourselves.

**Recommended package**: `UUIDNext` by [@mareek](https://github.com/mareek/UUIDNext)
- **NuGet**: `UUIDNext` (latest stable: 1.0.0+)
- **License**: MIT
- **Features**: 
  - RFC 9562-compliant UUIDv7
  - Thread-safe, monotonic within same millisecond
  - Optional custom timestamp provider (testability)
  - Zero dependencies
  - .NET 6+ support

**Alternative**: `Cysharp.Ulid` (if ULID preferred over UUIDv7, but UUIDv7 is more standards-aligned)

### 2. Centralized ID Generation

**Pattern**: Factory method in shared base class or static utility

**Option A: Static helper (recommended for minimal changes)**

```csharp
// File: MrWhoOidc.Auth/Persistence/GuidHelper.cs
using UUIDNext;

namespace MrWhoOidc.Auth.Persistence;

public static class GuidHelper
{
    /// <summary>
    /// Generates a time-ordered UUIDv7 (RFC 9562) for use as primary key.
    /// Provides better database index performance compared to random UUIDs.
    /// </summary>
    public static Guid NewId() => Uuid.NewDatabaseFriendly(Database.PostgreSql);
    
    // Legacy method for explicit compatibility; internally delegates to NewId()
    [Obsolete("Use NewId() instead. Will be removed in v2.0")]
    public static Guid NewGuid() => NewId();
}
```

**Option B: Base entity class (more invasive, better encapsulation)**

```csharp
// File: MrWhoOidc.Auth/Persistence/EntityBase.cs
namespace MrWhoOidc.Auth.Persistence;

public abstract class EntityBase
{
    public Guid Id { get; set; } = GuidHelper.NewId();
}

// All entities inherit: public class User : EntityBase { ... }
```

**Recommendation**: Start with **Option A** (static helper) to minimize refactoring; migrate to Option B in future major version if desired.

### 3. Entity Default Value Migration

**Before (current)**:
```csharp
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // ...
}
```

**After (UUIDv7)**:
```csharp
public class User
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    // ...
}
```

**Migration strategy**:
1. Add `UUIDNext` package reference to `MrWhoOidc.Auth.csproj`
2. Create `GuidHelper.cs` static utility
3. Replace all `= Guid.NewGuid()` with `= GuidHelper.NewId()` across entity classes
4. Run full test suite to ensure compatibility
5. No database migration required (existing UUIDs remain valid, new records get UUIDv7)

### 4. PostgreSQL Considerations

**Column type**: No change; remains `uuid`

**Index impact**: Expect ~80-90% reduction in page splits for insert-heavy tables (e.g., `Tokens`, `AuthorizationCodes`, `BackchannelLogoutNotifications`)

**Monitoring queries** (PostgreSQL 12+):
```sql
-- Check index bloat (compare before/after UUIDv7 adoption)
SELECT schemaname, tablename, pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) AS size
FROM pg_tables
WHERE schemaname = 'public'
ORDER BY pg_total_relation_size(schemaname||'.'||tablename) DESC;

-- Monitor page splits (requires pg_stat_statements extension)
SELECT query, calls, total_time, blk_read_time, blk_write_time
FROM pg_stat_statements
WHERE query LIKE '%INSERT INTO%'
ORDER BY total_time DESC
LIMIT 10;
```

**PostgreSQL 17+ bonus**: Native `uuid_extract_time(uuid)` function for UUIDv7 timestamp extraction (useful for debugging/analytics).

### 5. Backward Compatibility

**Existing data**: All existing UUIDv4 primary keys remain unchanged and valid; PostgreSQL treats them identically to UUIDv7 at the type level.

**API contracts**: No changes; clients continue to receive/send RFC 4122-formatted UUID strings (e.g., `"550e8400-e29b-41d4-a716-446655440000"`).

**Foreign keys**: FKs work transparently; mix of UUIDv4/UUIDv7 in same table is fine (e.g., new `Token` with UUIDv7 ID referencing old `User` with UUIDv4 ID).

**Sorting behavior**: UUIDv7 provides approximate chronological ordering by ID (millisecond precision); existing `CreatedAt` columns remain authoritative for audit/timestamps.

---

## Implementation Epics & Tasks

**Status legend**:
- [ ] Not started
- [~] In progress
- [x] Done

### Epic 0: Research & Validation

- [ ] **Task 0.1**: Benchmark UUIDv4 vs UUIDv7 insert performance
  - Set up isolated PostgreSQL instance with realistic schema
  - Insert 1M+ records using both strategies
  - Measure: insert time, index size, page splits (via `pgstattuple`)
  - Document results in `docs/uuidv7-benchmark-results.md`
  - **Owner**: TBD
  - **Estimate**: 4 hours

- [ ] **Task 0.2**: Evaluate UUIDv7 library options
  - Compare: `UUIDNext`, `Cysharp.Ulid`, `uuid7-csharp`, custom implementation
  - Criteria: RFC 9562 compliance, performance, thread-safety, testability, maintenance
  - Document decision in ADR (Architecture Decision Record)
  - **Owner**: TBD
  - **Estimate**: 2 hours

- [ ] **Task 0.3**: Validate Npgsql/EF Core compatibility
  - Create spike branch with UUIDv7 in one entity (e.g., `Token`)
  - Verify: migrations work, queries return correct types, no truncation/casting issues
  - Test on PostgreSQL 13, 14, 15, 16 (CI matrix if possible)
  - **Owner**: TBD
  - **Estimate**: 3 hours

### Epic 1: Core Infrastructure

- [ ] **Task 1.1**: Add UUIDNext package reference
  - Update `MrWhoOidc.Auth/MrWhoOidc.Auth.csproj`
  - Pin to specific version (e.g., `<PackageReference Include="UUIDNext" Version="1.0.0" />`)
  - Run `dotnet restore` and verify no conflicts
  - **Owner**: TBD
  - **Estimate**: 0.5 hours

- [ ] **Task 1.2**: Create `GuidHelper` utility class
  - File: `MrWhoOidc.Auth/Persistence/GuidHelper.cs`
  - Implement `NewId()` using `Uuid.NewDatabaseFriendly(Database.PostgreSql)`
  - Add XML docs explaining UUIDv7 benefits
  - Add unit tests: verify format (`version=7`), monotonicity, thread-safety
  - **Owner**: TBD
  - **Estimate**: 2 hours

- [ ] **Task 1.3**: Add integration tests for mixed UUID types
  - Test: Create entity with UUIDv7 ID referencing entity with UUIDv4 FK
  - Test: Query/filter/sort by mixed UUIDs
  - Test: Verify EF change tracking with UUIDv7 IDs
  - **Owner**: TBD
  - **Estimate**: 3 hours

### Epic 2: Entity Migration (Core Domain)

- [ ] **Task 2.1**: Migrate tenant & identity entities
  - Entities: `Tenant`, `User`, `Realm`, `Role`, `Scope`
  - Replace `Guid.NewGuid()` → `GuidHelper.NewId()`
  - Run existing unit tests; no new migrations needed
  - **Owner**: TBD
  - **Estimate**: 1 hour

- [ ] **Task 2.2**: Migrate client entities
  - Entities: `Client`, `ClientSecret`, `ClientScope`, `ClientIdentityProvider`, `ClientJwksHistory`
  - Update and test
  - **Owner**: TBD
  - **Estimate**: 1 hour

- [ ] **Task 2.3**: Migrate user assignment entities
  - Entities: `UserAlternativeEmail`, `UserClientAssignment`, `UserRoleAssignment`, `UserRealmRoleAssignment`, `UserClientRoleAssignment`
  - Update and test
  - **Owner**: TBD
  - **Estimate**: 1 hour

### Epic 3: Entity Migration (Protocol & Audit)

- [ ] **Task 3.1**: Migrate protocol state entities
  - Entities: `AuthorizationCode`, `Token`, `Consent`, `PushedAuthorizationRequest`, `QrLoginSession`, `LogoutRedirectReference`
  - Critical: Verify token revocation still works with mixed UUID formats
  - **Owner**: TBD
  - **Estimate**: 2 hours

- [ ] **Task 3.2**: Migrate federation entities
  - Entities: `IdentityProvider`, `IdentityProviderClaimMapping`, `IdentityProviderKey`, `ExternalIdentity`
  - Test IdP chaining flows end-to-end
  - **Owner**: TBD
  - **Estimate**: 1.5 hours

- [ ] **Task 3.3**: Migrate security & audit entities
  - Entities: `SigningKey`, `RevocationAudit`, `BackchannelLogoutNotification`, `ImpersonationAuditLog`, `Registration`, `DataProtectionKey`
  - Verify BCL dispatcher continues to work
  - Verify Data Protection key storage/retrieval
  - **Owner**: TBD
  - **Estimate**: 2 hours

### Epic 4: Testing & Validation

- [ ] **Task 4.1**: Run full unit test suite
  - Execute: `dotnet test` (all projects)
  - Target: 100% existing tests pass
  - Fix any failures related to ID generation/comparison
  - **Owner**: TBD
  - **Estimate**: 1 hour

- [ ] **Task 4.2**: Run integration/E2E tests
  - Aspire AppHost-based E2E scenarios
  - Verify: OIDC flows, admin UI CRUD, BCL dispatch, IdP chaining
  - **Owner**: TBD
  - **Estimate**: 2 hours

- [ ] **Task 4.3**: Performance validation (insert benchmarks)
  - Set up load test: 100K token issuances, 50K user registrations
  - Compare: before (UUIDv4) vs after (UUIDv7) insert throughput
  - Measure: avg latency, p95/p99, index size growth
  - Document results in `docs/uuidv7-performance-results.md`
  - **Owner**: TBD
  - **Estimate**: 4 hours

- [ ] **Task 4.4**: Database migration dry-run
  - Deploy to staging environment with production-like data volume
  - Monitor: no errors, query performance unchanged, index sizes
  - Rollback test: verify app works if reverted to UUIDv4 code (existing UUIDs still valid)
  - **Owner**: TBD
  - **Estimate**: 3 hours

### Epic 5: Documentation & Observability

- [ ] **Task 5.1**: Update architecture docs
  - Document: `docs/developer-guide.md`, `docs/copilot-instructions.md`
  - Add section: "Primary Key Generation Strategy (UUIDv7)"
  - Explain: why UUIDv7, how to generate IDs in new entities, troubleshooting
  - **Owner**: TBD
  - **Estimate**: 1 hour

- [ ] **Task 5.2**: Add ID generation metrics
  - Instrument `GuidHelper.NewId()` with counter metric (optional)
  - Expose via OpenTelemetry if monitoring UUIDv7 adoption rate
  - **Owner**: TBD
  - **Estimate**: 1.5 hours

- [ ] **Task 5.3**: Create runbook for rollback
  - Document: How to revert to `Guid.NewGuid()` if issues arise
  - Note: Existing UUIDv7 records remain valid; only new records use UUIDv4 again
  - Include: monitoring queries, known gotchas
  - **Owner**: TBD
  - **Estimate**: 1 hour

### Epic 6: Deployment & Monitoring

- [ ] **Task 6.1**: Deploy to staging
  - Update NuGet packages, deploy code
  - Run smoke tests: login, token issuance, admin CRUD
  - Monitor: error logs, insert latency, index bloat
  - **Owner**: TBD
  - **Estimate**: 2 hours

- [ ] **Task 6.2**: Gradual production rollout (optional canary)
  - Option: Deploy to 10% of instances first, monitor 24-48h
  - Compare: insert performance, error rates vs baseline
  - **Owner**: TBD
  - **Estimate**: 4 hours (includes monitoring window)

- [ ] **Task 6.3**: Post-deployment validation
  - Run production smoke tests
  - Verify: Token issuance works, admin UI functional, no UUID parsing errors in logs
  - Check database: Confirm new records have UUIDv7 format (`SELECT id FROM users ORDER BY created_at DESC LIMIT 10;` and inspect version bits)
  - **Owner**: TBD
  - **Estimate**: 1 hour

---

## Testing Strategy

### Unit Tests

**File**: `MrWhoOidc.UnitTests/GuidHelperTests.cs` (new)

```csharp
[TestClass]
public class GuidHelperTests
{
    [TestMethod]
    public void NewId_GeneratesValidUUIDv7()
    {
        var id = GuidHelper.NewId();
        
        // Extract version field (bits 48-51 should be 0111 = 7)
        var bytes = id.ToByteArray();
        var version = (bytes[7] >> 4) & 0x0F;
        
        Assert.AreEqual(7, version, "Generated UUID should have version=7");
    }
    
    [TestMethod]
    public void NewId_IsMonotonicWithinMillisecond()
    {
        var ids = Enumerable.Range(0, 1000)
            .Select(_ => GuidHelper.NewId())
            .ToList();
        
        // UUIDv7 with same timestamp should be monotonic via random increment
        var timestamps = ids.Select(ExtractTimestamp).ToList();
        Assert.IsTrue(timestamps.SequenceEqual(timestamps.OrderBy(x => x)));
    }
    
    [TestMethod]
    public void NewId_IsThreadSafe()
    {
        var ids = new ConcurrentBag<Guid>();
        
        Parallel.For(0, 10000, _ => ids.Add(GuidHelper.NewId()));
        
        Assert.AreEqual(10000, ids.Distinct().Count(), "All generated UUIDs should be unique");
    }
    
    private static long ExtractTimestamp(Guid uuid)
    {
        var bytes = uuid.ToByteArray();
        // Extract 48-bit timestamp (big-endian)
        return ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | 
               ((long)bytes[2] << 24) | ((long)bytes[3] << 16) |
               ((long)bytes[4] << 8) | bytes[5];
    }
}
```

### Integration Tests

- Verify mixed UUIDv4/UUIDv7 foreign keys work
- Test entity CRUD operations (create with UUIDv7, query, update, delete)
- Verify JSON serialization (API responses) unchanged

### Load Tests

- Compare insert performance: 100K tokens with UUIDv4 vs UUIDv7
- Monitor PostgreSQL `pg_stat_user_indexes` for index bloat reduction

---

## Risks & Mitigations

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|------------|
| UUIDv7 library has bugs/security issues | High | Low | Use well-vetted library (`UUIDNext` has 1M+ downloads); add fallback to `Guid.NewGuid()` if critical bug found |
| Performance regression on reads | Medium | Low | UUIDv7 improves index locality for writes/inserts; reads should be neutral or faster. Benchmark before/after. |
| Timestamp leakage concerns | Low | Medium | UUIDv7 embeds timestamp (48-bit ms precision); acceptable for internal IDs. Do NOT use for security-sensitive secrets. |
| Mixed UUID format confusion | Low | Low | Both formats are valid UUIDs; PostgreSQL handles transparently. Document in code comments. |
| Rollback complexity | Medium | Low | No schema migration; reverting code to `Guid.NewGuid()` is safe. Existing UUIDv7 records remain valid. |

---

## Success Metrics

1. **Insert performance**: ≥50% reduction in insert latency for high-volume tables (`Tokens`, `AuthorizationCodes`)
2. **Index size**: ≤10% growth in index size over 6 months (vs projected 15-20% with UUIDv4)
3. **Page splits**: ≥80% reduction in B-tree page splits (via `pg_stat_user_indexes`)
4. **Zero regressions**: 100% existing unit/integration tests pass
5. **API compatibility**: No breaking changes to external API contracts

---

## Timeline Estimate

**Assumptions**: Single engineer, part-time (4 hours/day)

| Epic | Tasks | Estimated Hours | Days (4h/day) |
|------|-------|-----------------|---------------|
| Epic 0: Research & Validation | 3 | 9 | 2.25 |
| Epic 1: Core Infrastructure | 3 | 5.5 | 1.5 |
| Epic 2: Entity Migration (Core) | 3 | 3 | 0.75 |
| Epic 3: Entity Migration (Protocol) | 3 | 5.5 | 1.5 |
| Epic 4: Testing & Validation | 4 | 10 | 2.5 |
| Epic 5: Documentation | 3 | 3.5 | 1 |
| Epic 6: Deployment | 3 | 7 | 1.75 |
| **Total** | **22 tasks** | **43.5 hours** | **~11 days** |

**Calendar time**: ~2-3 weeks with review/testing buffer

---

## References

### UUIDv7 Specification
- RFC 9562: Universally Unique IDentifiers (UUIDs) - https://www.rfc-editor.org/rfc/rfc9562.html
- UUIDv7 design rationale - https://www.ietf.org/archive/id/draft-ietf-uuidrev-rfc4122bis-14.html#name-uuidv7-layout-and-bit-order

### Libraries
- UUIDNext (C# implementation) - https://github.com/mareek/UUIDNext
- Npgsql UUID mapping - https://www.npgsql.org/doc/types/basic.html#guid-and-uuid

### Performance Studies
- PostgreSQL UUID vs ULID benchmarks - https://supabase.com/blog/choosing-a-postgres-primary-key
- Index locality and B-tree page splits - https://www.cybertec-postgresql.com/en/uuid-serial-or-identity-columns-for-postgresql-auto-generated-primary-keys/
- Cloudflare blog: "Generating good unique IDs" - https://blog.cloudflare.com/generating-good-unique-ids-in-go/

### Related MrWhoOidc Docs
- `docs/developer-guide.md` - Current architecture
- `docs/copilot-instructions.md` - Codebase conventions
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` - Entity definitions
- `docs/client-secret-rotation-backlog.md` - Example backlog format

---

## Appendix A: UUIDv7 Format Breakdown

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                        unix_ts_ms (48 bits)                    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|          unix_ts_ms           |  ver  |       rand_a (12)     |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|var|                    rand_b (62 bits)                        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                         rand_b (cont.)                         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

- **unix_ts_ms** (48 bits): Milliseconds since Unix epoch (Jan 1, 1970)
- **ver** (4 bits): Version = 0111 (7 in decimal)
- **rand_a** (12 bits): Random data
- **var** (2 bits): Variant = 10 (RFC 4122)
- **rand_b** (62 bits): Additional random data

**Total randomness**: 74 bits (12 + 62)  
**Timestamp precision**: 1 millisecond  
**Expiry**: ~8,900 years from Unix epoch (2^48 milliseconds)

**Example UUIDv7**:  
`018b7f3e-4f5a-7c3d-9f2e-1a4b6c8d0e2f`
- Timestamp: `018b7f3e4f5a` = 1,697,123,456,789 ms = ~2023-10-12 12:34:56 UTC
- Version: `7` (from `7c3d`)
- Variant: `10` (from `9f2e`)
- Random: `c3d 9f2e 1a4b6c8d0e2f` (74 bits)

---

## Appendix B: Code Review Checklist

Use this checklist during implementation review:

- [ ] All entity classes use `GuidHelper.NewId()` instead of `Guid.NewGuid()`
- [ ] `GuidHelper` class has XML docs explaining UUIDv7 benefits
- [ ] Unit tests verify UUIDv7 format (version=7, variant=10)
- [ ] Integration tests cover mixed UUIDv4/UUIDv7 foreign keys
- [ ] No database migrations generated (type remains `uuid`)
- [ ] Performance benchmarks show improvement (or neutral)
- [ ] Documentation updated (`developer-guide.md`, `copilot-instructions.md`)
- [ ] No API contract changes (UUIDs still serialized as RFC 4122 strings)
- [ ] Rollback plan documented (revert to `Guid.NewGuid()`)
- [ ] All existing tests pass (run `dotnet test`)

---

## Appendix C: FAQ

**Q: Do we need to migrate existing UUIDs to UUIDv7?**  
A: No. Existing UUIDv4 records remain valid and functional. Only new records will use UUIDv7 generation.

**Q: Will queries break with mixed UUID versions?**  
A: No. PostgreSQL's `uuid` type is version-agnostic. Both UUIDv4 and UUIDv7 are treated identically for comparisons, indexes, and foreign keys.

**Q: Can we still generate random UUIDs if needed?**  
A: Yes. `Guid.NewGuid()` remains available for use cases requiring non-sequential IDs (e.g., security tokens, opaque handles). Use `GuidHelper.NewId()` for primary keys.

**Q: How do we identify UUIDv7 vs UUIDv4 in the database?**  
A: Check the version nibble (4 bits at position 48-51). UUIDv7 has version=7 (`0111`), UUIDv4 has version=4 (`0100`).

**Q: Does this affect API responses?**  
A: No. UUIDs are serialized to strings using RFC 4122 format (e.g., `"550e8400-e29b-41d4-a716-446655440000"`). Clients cannot distinguish UUIDv7 from UUIDv4 without parsing the version bits.

**Q: What if the UUIDNext library is abandoned?**  
A: The UUIDv7 algorithm is simple (~50 lines of code) and well-documented in RFC 9562. We can vendor the implementation if needed. Alternatively, PostgreSQL 17+ supports `gen_random_uuid(7)` natively.

**Q: Will sorting by ID now reflect creation order?**  
A: Approximately, with millisecond precision. For authoritative timestamps, continue using explicit `CreatedAt`/`UpdatedAt` columns. UUIDv7 ordering is a performance optimization, not a semantic guarantee.

**Q: Any security concerns with embedded timestamps?**  
A: UUIDv7 reveals approximate creation time (±1ms). This is acceptable for internal primary keys. Do NOT use UUIDv7 for security-sensitive secrets (session tokens, API keys, etc.) where timing leakage is a concern. Use `Guid.NewGuid()` or cryptographically random bytes for those cases.

---

**END OF DOCUMENT**
