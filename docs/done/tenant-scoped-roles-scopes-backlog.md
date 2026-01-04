# Backlog: Tenant-Scoped Roles and Scopes

## Date: October 11, 2025
## Priority: HIGH
## Epic: Multi-Tenancy Enhancement

## Executive Summary

**Current State:** Roles have `TenantId` but Scopes are global/shared across all tenants.

**Desired State:** Both Roles AND Scopes should be fully tenant-scoped, allowing each tenant to define their own custom roles and scopes while maintaining standard OAuth2/OIDC scopes globally.

**Business Justification:**
- **Flexibility:** Different tenants need different permission models
- **Isolation:** Tenant A shouldn't see Tenant B's custom roles/scopes
- **Compliance:** Some industries require strict data separation
- **Self-Service:** Tenant admins should manage their own authorization model

---

## Current Implementation Analysis

### Roles (Partially Tenant-Scoped) ✅ + ⚠️

**Schema:**
```csharp
public class Role
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }  // ✅ Has TenantId
    public Guid RealmId { get; set; }
    public string Name { get; set; }
    public bool IsActive { get; set; }
}
```

**Status:**
- ✅ Database schema supports tenant separation
- ✅ API endpoints filter by tenant (after security fix)
- ✅ UI shows only current tenant's roles
- ⚠️ **Issue:** Standard roles (e.g., "admin", "user") duplicated per tenant
- ⚠️ **Issue:** No global role catalog for common roles

**Recommendations:**
1. Introduce `IsGlobal` flag for standard roles
2. Support role inheritance/templates
3. Allow tenant admins to create custom roles
4. Platform admins manage global role catalog

### Scopes (Currently Global) ❌

**Schema:**
```csharp
public class Scope
{
    [Key]
    public string Name { get; set; }        // No TenantId
    public string? Description { get; set; }
    public bool IsExposed { get; set; }
}
```

**Status:**
- ❌ No `TenantId` field - all scopes are global
- ❌ Only platform admins can create/edit/delete scopes
- ❌ Tenant admins cannot create custom scopes
- ✅ Works well for standard OAuth2/OIDC scopes (`openid`, `profile`, `email`)

**Issues:**
1. Tenant A cannot create custom scopes like `tenant-a.admin` or `tenant-a.reports.read`
2. All tenants share same scope namespace (risk of conflicts)
3. API resources need to know which tenant issued the token to validate custom scopes
4. No flexibility for tenant-specific permission models

---

## Proposed Solution: Hybrid Global + Tenant-Scoped Model

### Design Principles

1. **Standard Scopes Remain Global:** `openid`, `profile`, `email`, `offline_access`, `roles` etc.
2. **Custom Scopes Are Tenant-Scoped:** Each tenant can define their own
3. **Clear Naming Convention:** Prevent scope name collisions
4. **Backward Compatible:** Existing scopes migrate to global
5. **Admin Hierarchy:** Platform admins manage global, tenant admins manage tenant-scoped

---

## Implementation Plan

### Phase 1: Database Schema Migration (CRITICAL)

#### 1.1 Add TenantId to Scopes

**Migration Steps:**
1. Add nullable `TenantId` column to `Scopes` table
2. Add `IsGlobal` flag (default: false)
3. Migrate existing scopes to global (`TenantId = NULL`, `IsGlobal = true`)
4. Add composite unique index: `(TenantId, Name)` to prevent duplicate names per tenant

**New Schema:**
```csharp
public class Scope
{
    [Key]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    
    // Multi-tenancy support
    public Guid? TenantId { get; set; }  // NULL for global scopes
    public bool IsGlobal { get; set; } = false;  // True for standard OAuth2 scopes
    
    [MaxLength(200)]
    public string? Description { get; set; }
    
    public bool IsExposed { get; set; } = true;
}
```

**EF Core Configuration:**
```csharp
modelBuilder.Entity<Scope>()
    .HasIndex(s => new { s.TenantId, s.Name })
    .IsUnique()
    .HasFilter("[TenantId] IS NOT NULL");  // Allow multiple global scopes with same name

modelBuilder.Entity<Scope>()
    .HasIndex(s => s.Name)
    .IsUnique()
    .HasFilter("[TenantId] IS NULL AND [IsGlobal] = 1");  // Enforce unique names for global scopes
```

**Migration Command:**
```bash
dotnet ef migrations add AddTenantScopesToScopes \
  --project MrWhoOidc.Auth \
  --startup-project MrWhoOidc.WebAuth \
  --output-dir Persistence/Migrations
```

**Data Migration Script:**
```sql
-- Mark all existing scopes as global
UPDATE Scopes 
SET IsGlobal = 1, 
    TenantId = NULL
WHERE TenantId IS NULL OR IsGlobal = 0;
```

#### 1.2 Enhance Roles Schema (Optional)

Consider adding `IsGlobal` flag to Roles for standard role templates:

```csharp
public class Role
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }  // NULL for global role templates
    public bool IsGlobal { get; set; } = false;
    public Guid RealmId { get; set; }
    public string Name { get; set; }
    public bool IsActive { get; set; }
}
```

**Use Case:** Platform admin creates "admin" role template → Tenants inherit/customize

---

### Phase 2: Business Logic Updates

#### 2.1 Scope Resolution Service

Create service to resolve scopes based on context:

```csharp
public interface IScopeResolver
{
    /// <summary>
    /// Get all scopes visible to the current context (global + tenant-specific)
    /// </summary>
    Task<IReadOnlyList<Scope>> GetAvailableScopesAsync(Guid? tenantId);
    
    /// <summary>
    /// Validate that requested scopes exist and are accessible
    /// </summary>
    Task<ScopeValidationResult> ValidateScopesAsync(
        IEnumerable<string> requestedScopes, 
        Guid? tenantId);
    
    /// <summary>
    /// Check if a scope name is available for creation
    /// </summary>
    Task<bool> IsScopeNameAvailableAsync(string scopeName, Guid? tenantId);
}

public class ScopeResolver(AuthDbContext db) : IScopeResolver
{
    public async Task<IReadOnlyList<Scope>> GetAvailableScopesAsync(Guid? tenantId)
    {
        var query = db.Scopes.AsNoTracking();
        
        if (tenantId.HasValue)
        {
            // Return global scopes + tenant-specific scopes
            query = query.Where(s => s.IsGlobal || s.TenantId == tenantId.Value);
        }
        else
        {
            // Return only global scopes
            query = query.Where(s => s.IsGlobal);
        }
        
        return await query.OrderBy(s => s.Name).ToListAsync();
    }
    
    public async Task<ScopeValidationResult> ValidateScopesAsync(
        IEnumerable<string> requestedScopes, 
        Guid? tenantId)
    {
        var scopeNames = requestedScopes.ToList();
        var availableScopes = await GetAvailableScopesAsync(tenantId);
        var availableScopeNames = availableScopes.Select(s => s.Name).ToHashSet();
        
        var validScopes = scopeNames.Where(s => availableScopeNames.Contains(s)).ToList();
        var invalidScopes = scopeNames.Except(validScopes).ToList();
        
        return new ScopeValidationResult
        {
            ValidScopes = validScopes,
            InvalidScopes = invalidScopes,
            IsValid = invalidScopes.Count == 0
        };
    }
    
    public async Task<bool> IsScopeNameAvailableAsync(string scopeName, Guid? tenantId)
    {
        if (tenantId.HasValue)
        {
            // Check if scope exists globally OR in this tenant
            return !await db.Scopes.AnyAsync(s => 
                s.Name == scopeName && 
                (s.IsGlobal || s.TenantId == tenantId.Value));
        }
        
        // Global scope - check only global namespace
        return !await db.Scopes.AnyAsync(s => s.Name == scopeName && s.IsGlobal);
    }
}
```

#### 2.2 Token Issuance Updates

**Authorization Handler (`AuthorizeHandler.cs`):**
- Use `IScopeResolver` to validate requested scopes
- Include tenant context in scope validation
- Emit tenant ID in access tokens for custom scope validation

**Token Generation:**
```csharp
// In token generation, include tenant ID for custom scopes
var customScopes = grantedScopes.Where(s => !IsStandardScope(s)).ToList();
if (customScopes.Any())
{
    claims.Add(new Claim("tenant_id", tenantId.ToString()));
}
```

#### 2.3 Token Validation (API Side)

**DPoP and Token Validation:**
APIs need to validate custom scopes against issuing tenant:

```csharp
public class TenantAwareScopeValidator
{
    public async Task<bool> ValidateScopeAsync(
        string scope, 
        string accessToken,
        AuthDbContext db)
    {
        // Extract tenant_id from token
        var tenantId = GetTenantIdFromToken(accessToken);
        
        // Check if scope exists globally OR in that tenant
        return await db.Scopes.AnyAsync(s => 
            s.Name == scope && 
            (s.IsGlobal || s.TenantId == tenantId));
    }
}
```

---

### Phase 3: Admin UI Updates

#### 3.1 Scopes Management UI

**Index Page:**
- Show global scopes (read-only for tenant admins)
- Show tenant-specific scopes (editable by tenant admins)
- Visual distinction (badges: "Global" vs "Custom")
- Platform admins see all scopes with tenant filter

**Add/Edit Pages:**
- Tenant admins can only create tenant-scoped scopes
- Platform admins can choose: Global or Tenant-specific
- Naming validation prevents conflicts

**Authorization Changes:**
```csharp
// Index.cshtml.cs - Both roles can view
[Authorize(Policy = "tenant-admin")]
public class IndexModel { }

// Add.cshtml.cs - Both roles can add (but different scopes)
[Authorize(Policy = "tenant-admin")]
public class AddModel 
{
    public async Task<IActionResult> OnPostAsync()
    {
        var isPlatformAdmin = await IsPlatformAdminAsync();
        
        if (isPlatformAdmin && Input.IsGlobal)
        {
            // Create global scope
            db.Scopes.Add(new Scope 
            { 
                Name = Input.Name, 
                TenantId = null, 
                IsGlobal = true 
            });
        }
        else
        {
            // Create tenant-scoped scope
            var currentTenantId = tenantAccessor.CurrentTenant.TenantId;
            db.Scopes.Add(new Scope 
            { 
                Name = Input.Name, 
                TenantId = currentTenantId, 
                IsGlobal = false 
            });
        }
        
        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/Scopes");
    }
}
```

#### 3.2 Roles Management UI

**Current State:** Already filtered by tenant ✅

**Enhancements:**
1. Add "Create from Template" feature for global roles
2. Add "IsGlobal" flag support if implementing role templates
3. Show inherited permissions from global roles

#### 3.3 Client Scope Assignment

**Client Edit Page:**
- Show available scopes = global + tenant-specific
- Visual grouping: "Standard Scopes" vs "Custom Scopes"
- Clear indication of scope source

---

### Phase 4: Naming Conventions & Validation

#### 4.1 Scope Naming Rules

**Global Scopes (Platform Admin Only):**
- Standard OAuth2: `openid`, `profile`, `email`, `offline_access`, `roles`, `phone`, `address`
- Generic API: `api`, `api.read`, `api.write`
- No tenant prefix allowed

**Tenant-Scoped Scopes (Tenant Admin):**
- **Option A: Free-form** - Any name not conflicting with global
  - Pro: Simple, flexible
  - Con: Risk of confusion across tenants
  
- **Option B: Mandatory prefix** - `{tenant-slug}.{scope-name}`
  - Pro: Clear ownership, prevents conflicts
  - Con: Verbose, harder to type
  - Example: `pop-app.reports.read`, `pop-app.admin`
  
- **Option C: Namespace** - `custom/{scope-name}` or `tenant/{scope-name}`
  - Pro: Clear distinction from standard scopes
  - Con: Less intuitive for end users

**Recommendation:** Option B (Mandatory tenant prefix)
- Enforce via validation: `{tenant-slug}.{suffix}`
- Auto-prefix in UI when tenant admin creates scope
- Validate suffix follows pattern: `^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$`

#### 4.2 Validation Rules

```csharp
public class ScopeNameValidator
{
    private static readonly string[] ReservedPrefixes = 
    {
        "openid", "profile", "email", "offline_access", 
        "roles", "api", "phone", "address"
    };
    
    public ValidationResult ValidateScopeName(
        string scopeName, 
        bool isGlobal, 
        string? tenantSlug)
    {
        // Global scopes: no restrictions (platform admin trusted)
        if (isGlobal)
        {
            if (ReservedPrefixes.Contains(scopeName))
                return ValidationResult.Success();
            
            // Prevent global scopes with tenant prefixes
            if (scopeName.Contains('.'))
                return ValidationResult.Error(
                    "Global scopes should not use dot notation");
        }
        else
        {
            // Tenant-scoped: enforce prefix
            if (!scopeName.StartsWith($"{tenantSlug}."))
                return ValidationResult.Error(
                    $"Tenant scope must start with '{tenantSlug}.'");
            
            var suffix = scopeName.Substring(tenantSlug.Length + 1);
            if (!Regex.IsMatch(suffix, @"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$"))
                return ValidationResult.Error(
                    "Invalid scope name format");
        }
        
        return ValidationResult.Success();
    }
}
```

---

### Phase 5: API Validation & Token Validation

#### 5.1 Authorization Middleware Enhancement

**Current:** APIs validate scopes from token claims

**Update Needed:**
```csharp
public class TenantAwareScopeRequirement : IAuthorizationRequirement
{
    public string RequiredScope { get; set; }
}

public class TenantAwareScopeHandler(
    AuthDbContext db) : AuthorizationHandler<TenantAwareScopeRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAwareScopeRequirement requirement)
    {
        var scopeClaim = context.User.FindFirst("scope")?.Value;
        if (string.IsNullOrEmpty(scopeClaim))
        {
            context.Fail();
            return;
        }
        
        var scopes = scopeClaim.Split(' ');
        if (!scopes.Contains(requirement.RequiredScope))
        {
            context.Fail();
            return;
        }
        
        // For custom scopes, validate they exist in DB
        if (!IsStandardScope(requirement.RequiredScope))
        {
            var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value;
            if (tenantIdClaim == null || !Guid.TryParse(tenantIdClaim, out var tenantId))
            {
                context.Fail();
                return;
            }
            
            var scopeExists = await db.Scopes.AnyAsync(s => 
                s.Name == requirement.RequiredScope && 
                (s.IsGlobal || s.TenantId == tenantId));
            
            if (!scopeExists)
            {
                context.Fail();
                return;
            }
        }
        
        context.Succeed(requirement);
    }
    
    private bool IsStandardScope(string scope) =>
        new[] { "openid", "profile", "email", "offline_access", "roles" }
            .Contains(scope);
}
```

#### 5.2 API Usage Pattern

```csharp
[ApiController]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "RequireScope:pop-app.reports.read")]
    public IActionResult GetReports()
    {
        // Custom scope validated against pop-app tenant
        return Ok(reports);
    }
}
```

---

### Phase 6: Migration & Rollout Strategy

#### 6.1 Database Migration

**Step 1: Schema Update**
```bash
# 1. Create migration
dotnet ef migrations add AddTenantScopingToScopes --project MrWhoOidc.Auth

# 2. Review generated migration
# 3. Test on dev database
dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth

# 4. Verify existing scopes marked as global
```

**Step 2: Data Migration**
```sql
-- Mark all existing scopes as global
UPDATE Scopes 
SET IsGlobal = 1, 
    TenantId = NULL;

-- Verify
SELECT Name, IsGlobal, TenantId FROM Scopes;
```

#### 6.2 Code Rollout

**Phase 1: Backwards Compatible (Week 1)**
- Deploy schema changes
- Update scope resolution to support both models
- No UI changes yet
- All scopes remain global

**Phase 2: Enable Tenant Scopes (Week 2)**
- Deploy UI updates
- Enable tenant admins to create scopes
- Platform admins can still manage global scopes
- Monitor for naming conflicts

**Phase 3: Migration Support (Week 3-4)**
- Provide migration tool for tenants to convert global scopes to tenant-scoped
- Documentation and training
- Support requests

**Phase 4: Cleanup (Week 5+)**
- Remove deprecated code paths
- Optimize queries
- Performance testing

---

### Phase 7: Testing Strategy

#### 7.1 Unit Tests

```csharp
[TestClass]
public class TenantScopedScopeTests
{
    [TestMethod]
    public async Task TenantAdmin_CanCreateTenantScopedScope()
    {
        // Arrange
        var tenant = CreateTenant("pop-app");
        var tenantAdmin = CreateTenantAdmin(tenant);
        
        // Act
        var result = await CreateScope("pop-app.custom", tenant.Id);
        
        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(tenant.Id, result.Scope.TenantId);
        Assert.IsFalse(result.Scope.IsGlobal);
    }
    
    [TestMethod]
    public async Task TenantAdmin_CannotCreateGlobalScope()
    {
        // Arrange
        var tenant = CreateTenant("pop-app");
        var tenantAdmin = CreateTenantAdmin(tenant);
        
        // Act
        var result = await CreateGlobalScope("custom-global");
        
        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual("Forbidden", result.ErrorCode);
    }
    
    [TestMethod]
    public async Task TenantAdmin_CannotSeeOtherTenantScopes()
    {
        // Arrange
        var tenant1 = CreateTenant("tenant1");
        var tenant2 = CreateTenant("tenant2");
        await CreateScope("tenant2.secret", tenant2.Id);
        
        // Act
        var scopes = await GetAvailableScopes(tenant1.Id);
        
        // Assert
        Assert.IsFalse(scopes.Any(s => s.Name == "tenant2.secret"));
    }
    
    [TestMethod]
    public async Task ScopeResolver_ReturnsGlobalAndTenantScopes()
    {
        // Arrange
        var tenant = CreateTenant("pop-app");
        await CreateGlobalScope("openid");
        await CreateScope("pop-app.custom", tenant.Id);
        
        // Act
        var scopes = await scopeResolver.GetAvailableScopesAsync(tenant.Id);
        
        // Assert
        Assert.IsTrue(scopes.Any(s => s.Name == "openid" && s.IsGlobal));
        Assert.IsTrue(scopes.Any(s => s.Name == "pop-app.custom" && !s.IsGlobal));
    }
}
```

#### 7.2 Integration Tests

Test token flows with custom scopes:
1. Request token with tenant-scoped scope
2. Validate token contains scope
3. Use token to access API requiring custom scope
4. Verify cross-tenant scope isolation

#### 7.3 E2E Tests

Simulate tenant admin workflows:
1. Login as tenant admin
2. Navigate to Scopes page
3. Create custom scope
4. Assign scope to client
5. Test authorization flow
6. Verify API access

---

### Phase 8: Documentation

#### 8.1 Admin Guide Updates

**Section: Scope Management**
- Explain global vs tenant-scoped scopes
- Naming conventions
- Best practices
- Examples

#### 8.2 API Documentation

**For API Developers:**
- How to validate custom scopes
- Token structure with tenant_id claim
- Scope resolution logic
- Error handling

#### 8.3 Migration Guide

**For Existing Tenants:**
- What's changing
- How to create custom scopes
- Migration timeline
- Breaking changes (if any)

---

## Risk Assessment

### High Risks

1. **Breaking Changes for Existing Clients**
   - **Risk:** Clients configured with global scopes may break
   - **Mitigation:** Backwards compatibility, gradual rollout
   
2. **Token Validation Complexity**
   - **Risk:** APIs must understand tenant context for custom scopes
   - **Mitigation:** Clear documentation, helper libraries
   
3. **Scope Name Conflicts**
   - **Risk:** Tenant creates scope conflicting with future global scope
   - **Mitigation:** Enforce naming conventions, prefix requirements

4. **Performance Impact**
   - **Risk:** Additional DB queries for scope validation
   - **Mitigation:** Caching, optimized queries, scope resolution service

### Medium Risks

5. **Migration Complexity**
   - **Risk:** Existing data may not map cleanly to new model
   - **Mitigation:** Comprehensive migration scripts, rollback plan
   
6. **UI Confusion**
   - **Risk:** Users confused by global vs tenant-scoped scopes
   - **Mitigation:** Clear visual indicators, tooltips, documentation

7. **Testing Coverage**
   - **Risk:** Edge cases in multi-tenant scope resolution
   - **Mitigation:** Comprehensive test suite, canary deployments

### Low Risks

8. **Increased Storage**
   - **Risk:** More scope records in database
   - **Mitigation:** Minimal impact, scopes are small entities

---

## Success Metrics

### Phase 1 (Schema Migration)
- ✅ Zero downtime during migration
- ✅ All existing scopes marked as global
- ✅ No data loss

### Phase 2 (Feature Launch)
- ✅ 90%+ tenant admins can create custom scopes without support
- ✅ <5% error rate in scope validation
- ✅ <100ms overhead for scope resolution

### Phase 3 (Adoption)
- ✅ 50%+ active tenants create at least one custom scope
- ✅ <1% scope naming conflicts
- ✅ Zero cross-tenant scope leakage incidents

### Phase 4 (Performance)
- ✅ No degradation in token issuance time
- ✅ <10ms additional latency for API authorization
- ✅ Scope cache hit rate >95%

---

## Timeline Estimate

| Phase | Duration | Dependencies |
|-------|----------|--------------|
| 1. Schema Migration | 1 week | None |
| 2. Business Logic | 2 weeks | Phase 1 |
| 3. Admin UI | 2 weeks | Phase 2 |
| 4. Naming Conventions | 1 week | Phase 3 |
| 5. API Validation | 1 week | Phase 2 |
| 6. Testing | 2 weeks | Phases 2-5 |
| 7. Documentation | 1 week | All phases |
| 8. Rollout | 4 weeks | All phases |

**Total Estimated Duration:** 10-12 weeks (2.5-3 months)

---

## Open Questions

1. **Scope Inheritance:** Should child tenants inherit parent tenant scopes in hierarchical models?
2. **Scope Sharing:** Should tenants be able to share custom scopes with other tenants?
3. **Scope Marketplace:** Should there be a "marketplace" of pre-defined scope templates?
4. **Scope Versioning:** How to handle scope definition changes over time?
5. **Scope Deprecation:** What's the lifecycle for retiring old scopes?
6. **Role-Scope Mapping:** Should roles be mapped to scopes more explicitly?
7. **Dynamic Scopes:** Should scopes support wildcards or patterns (e.g., `tenant.*.read`)?

---

## Related Documents

- `docs/multitenancy-backlog.md` - Overall multi-tenancy roadmap
- `docs/scopes-global-resource-design.md` - Current global scope design
- `docs/tenant-separation-roles-security-fix.md` - Roles tenant filtering
- `docs/admin-guide.md` - Admin UI documentation

---

## Approval & Sign-off

- [ ] Product Owner Review
- [ ] Technical Architect Review
- [ ] Security Team Review
- [ ] DevOps Team Review
- [ ] Documentation Team Review

---

## Notes

- This is a significant architectural change requiring careful planning
- Consider running a pilot with 2-3 tenants before full rollout
- Budget extra time for edge cases and tenant-specific customization requests
- Plan for backwards compatibility for at least 6 months after launch
