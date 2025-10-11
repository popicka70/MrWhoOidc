# Tenant-Scoped Roles & Scopes - Quick Reference

## Current vs. Proposed State

| Aspect | Current State | Proposed State |
|--------|---------------|----------------|
| **Roles** | ✅ Tenant-scoped (has `TenantId`) | ✅ Enhanced with `IsGlobal` flag for templates |
| **Scopes** | ❌ Global only (no `TenantId`) | ✅ Hybrid: Global + Tenant-scoped |
| **Tenant Admin Powers** | Can manage roles, view scopes | Can manage both roles AND custom scopes |
| **Naming** | Free-form | Enforced prefix: `{tenant-slug}.{scope-name}` |

## Key Benefits

1. **Flexibility:** Each tenant defines their own custom scopes (e.g., `pop-app.reports.read`)
2. **Isolation:** Tenant A cannot see or use Tenant B's custom scopes
3. **Standards:** Global OAuth2 scopes (`openid`, `profile`) remain shared
4. **Self-Service:** Tenant admins empowered to create custom authorization models

## Implementation Phases

### Phase 1: Schema (1 week)
- Add `TenantId` (nullable) and `IsGlobal` to Scopes table
- Migrate existing scopes to global
- Create composite unique index

### Phase 2: Business Logic (2 weeks)
- `IScopeResolver` service for scope resolution
- Update token issuance to validate tenant-scoped scopes
- Include `tenant_id` claim for custom scopes

### Phase 3: Admin UI (2 weeks)
- Tenant admins can create custom scopes
- Visual distinction: "Global" vs "Custom" badges
- Naming validation enforces tenant prefix

### Phase 4: API Validation (1 week)
- Update authorization middleware
- Validate custom scopes against issuing tenant
- Cache scope lookups for performance

### Phase 5: Testing & Rollout (4 weeks)
- Unit, integration, E2E tests
- Gradual rollout with backwards compatibility
- Documentation and training

## Database Schema Changes

```sql
-- Add columns to Scopes table
ALTER TABLE Scopes ADD TenantId uniqueidentifier NULL;
ALTER TABLE Scopes ADD IsGlobal bit NOT NULL DEFAULT 0;

-- Mark existing scopes as global
UPDATE Scopes SET IsGlobal = 1, TenantId = NULL;

-- Create unique indexes
CREATE UNIQUE INDEX IX_Scopes_TenantId_Name 
ON Scopes(TenantId, Name) 
WHERE TenantId IS NOT NULL;

CREATE UNIQUE INDEX IX_Scopes_Global_Name 
ON Scopes(Name) 
WHERE TenantId IS NULL AND IsGlobal = 1;
```

## Naming Convention

### Global Scopes (Platform Admin)
- Standard OAuth2: `openid`, `profile`, `email`, `offline_access`, `roles`
- Generic API: `api`, `api.read`, `api.write`
- No dot notation, no tenant prefix

### Tenant Scopes (Tenant Admin)
- **Mandatory Format:** `{tenant-slug}.{scope-name}`
- **Examples:**
  - `pop-app.admin`
  - `pop-app.reports.read`
  - `pop-app.reports.write`
  - `default-tenant.custom-feature`

### Validation Regex
```regex
^[a-z][a-z0-9-]*\.[a-z0-9]([a-z0-9._-]*[a-z0-9])?$
```

## Code Examples

### Creating Tenant-Scoped Scope

```csharp
// Tenant admin creates custom scope
var tenantId = tenantAccessor.CurrentTenant.TenantId;
var scope = new Scope
{
    Name = "pop-app.reports.read",
    TenantId = tenantId,
    IsGlobal = false,
    Description = "Read access to reports",
    IsExposed = true
};

db.Scopes.Add(scope);
await db.SaveChangesAsync();
```

### Scope Resolution

```csharp
// Get available scopes for tenant
var scopes = await scopeResolver.GetAvailableScopesAsync(tenantId);
// Returns: openid, profile, email, pop-app.reports.read, pop-app.admin

// Validate requested scopes
var result = await scopeResolver.ValidateScopesAsync(
    new[] { "openid", "pop-app.reports.read" }, 
    tenantId);
// result.IsValid = true
```

### Token Validation in API

```csharp
[HttpGet]
[Authorize(Policy = "RequireScope:pop-app.reports.read")]
public IActionResult GetReports()
{
    // Middleware validates:
    // 1. Token has "pop-app.reports.read" in scope claim
    // 2. Scope exists in DB for issuing tenant
    // 3. Tenant ID matches token's tenant_id claim
    
    return Ok(reports);
}
```

## Migration Checklist

- [ ] **Week 1:** Create and test database migration
- [ ] **Week 2:** Deploy schema changes to production (backwards compatible)
- [ ] **Week 3-4:** Implement `IScopeResolver` and business logic
- [ ] **Week 5-6:** Update Admin UI for tenant scope creation
- [ ] **Week 7:** Update API authorization middleware
- [ ] **Week 8-9:** Comprehensive testing (unit, integration, E2E)
- [ ] **Week 10:** Create documentation and training materials
- [ ] **Week 11-12:** Phased rollout with selected pilot tenants
- [ ] **Week 13-14:** Full rollout and monitoring

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Breaking changes | Maintain backwards compatibility, gradual rollout |
| Scope name conflicts | Enforce naming conventions, auto-prefix |
| Performance degradation | Cache scope lookups, optimize queries |
| Cross-tenant leaks | Comprehensive testing, security audit |
| Migration failures | Rollback plan, dry-run on staging |

## Testing Strategy

### Unit Tests
- Scope resolution logic
- Naming validation
- Tenant filtering
- Global vs tenant-scoped behavior

### Integration Tests
- Token issuance with custom scopes
- API authorization with tenant scopes
- Cross-tenant isolation

### E2E Tests
- Tenant admin creates custom scope
- Assigns scope to client
- User authorizes and gets token
- API validates custom scope

## Success Criteria

- ✅ Zero cross-tenant scope leakage
- ✅ <5% error rate in scope validation
- ✅ 50%+ tenants adopt custom scopes within 3 months
- ✅ <100ms overhead for scope resolution
- ✅ 90%+ tenant admins can self-service without support

## Related Documents

- **Detailed Backlog:** `docs/tenant-scoped-roles-scopes-backlog.md`
- **Current Scopes Design:** `docs/scopes-global-resource-design.md`
- **Multi-Tenancy Guide:** `docs/multitenancy-quick-reference.md`
- **Admin Guide:** `docs/admin-guide.md`

## Open Questions for Product Team

1. Should we support scope wildcards? (e.g., `pop-app.*.read`)
2. Should tenants be able to share scopes with other tenants?
3. What's the approval process for tenant-created scopes?
4. Should we limit the number of custom scopes per tenant?
5. How do we handle scope deprecation/lifecycle?

## Timeline

**Fast Track:** 10 weeks (aggressive, requires dedicated team)
**Standard:** 12-14 weeks (realistic with other priorities)
**Conservative:** 16 weeks (safer, more testing time)

**Recommendation:** 12-14 weeks with phased rollout
