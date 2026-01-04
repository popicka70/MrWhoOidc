# Multi-Tenancy Quick Reference Card

**Date:** October 7, 2025 | **Phase:** 1 (95% Complete) → 2 (Starting Soon)

---

## 🎯 Current Status: THIS WEEK

### What's Done ✅
- Infrastructure (100%)
- Platform Admin UI (100%)
- User Portal (100%)
- Unit Tests (331 passing)

### What's Next 🔄
- **Monday-Tuesday:** E2E multi-tenant tests
- **Wednesday-Thursday:** Data isolation & security tests
- **Friday:** Documentation & wrap-up

### Goal for Friday Oct 11
- [ ] All E2E tests passing
- [ ] Phase 1 complete
- [ ] Ready for Phase 2

---

## 📊 Test Status

```
Unit Tests:        331/331 ✅ PASSING
Integration Tests:   0/50  🔄 TODO
E2E Tests:          0/20  🔄 TODO
```

**Priority Tests to Write:**
1. Create tenant → issue token → verify issuer
2. Cross-tenant isolation (User A ≠ User B)
3. Platform admin security
4. Mode switching (single ↔ multi)

---

## 🚀 Quick Commands

### Run All Tests
```powershell
dotnet test
```

### Run Specific Test Class
```powershell
dotnet test --filter "FullyQualifiedName~MultiTenantTests"
```

### Check Test Coverage
```powershell
dotnet test /p:CollectCoverage=true
```

### Start Development Server
```powershell
dotnet run --project MrWhoOidc.WebAuth
```

---

## 🔗 Important URLs (Local Dev)

| Service | URL | Notes |
|---------|-----|-------|
| Platform Admin | https://localhost:8443/PlatformAdmin | Requires `platform-admin` role |
| Tenant Admin | https://localhost:8443/t/{slug}/Admin | Requires `tenant-admin` role |
| User Portal | https://localhost:8443/Account | Any authenticated user |
| Default Tenant | https://localhost:8443/t/default | Fallback tenant |
| Discovery | https://localhost:8443/t/{slug}/.well-known/openid-configuration | Per-tenant |
| JWKS | https://localhost:8443/t/{slug}/jwks | Per-tenant keys |

---

## 📁 Key File Locations

### Platform Admin UI
```
MrWhoOidc.WebAuth/Pages/PlatformAdmin/
├── Index.cshtml[.cs]           # Dashboard
└── Tenants/
    ├── Index.cshtml[.cs]       # Tenant list
    ├── Create.cshtml[.cs]      # Create tenant
    └── Edit.cshtml[.cs]        # Edit tenant
```

### User Self-Service Portal
```
MrWhoOidc.WebAuth/Pages/Account/
├── Index.cshtml[.cs]           # Dashboard
├── Profile.cshtml[.cs]         # Profile management
├── Sessions.cshtml[.cs]        # Active sessions
├── Consents.cshtml[.cs]        # App permissions
├── LinkedAccounts.cshtml[.cs]  # External logins
└── Emails.cshtml[.cs]          # Alternative emails
```

### Multi-Tenancy Core
```
MrWhoOidc.Auth/MultiTenancy/
├── ITenantResolver.cs          # Resolve tenant from request
├── TenantAccessor.cs           # Scoped tenant context
├── TenantContext.cs            # Current tenant info
└── IssuerBuilder.cs            # Mode-aware issuer construction
```

### Tests
```
MrWhoOidc.UnitTests/
├── TenantResolutionTests.cs    # Tenant resolution logic
├── IssuerBuilderTests.cs       # Issuer construction
├── JwksEndpointTests.cs        # JWKS filtering
└── (Add E2E tests here)        # Multi-tenant flows
```

---

## 🧪 Test Templates

### E2E Test Template
```csharp
[TestClass]
public class MultiTenantE2ETests
{
    [TestMethod]
    public async Task CreateTenant_IssueToken_HasCorrectIssuer()
    {
        // Arrange: Setup test context
        var tenantSlug = "test-tenant";
        var expectedIssuer = $"https://localhost:8443/t/{tenantSlug}";

        // Act: Create tenant via Platform Admin
        // ... Platform Admin API call ...

        // Act: Issue token for tenant
        // ... Token issuance flow ...

        // Assert: Token issuer matches
        Assert.AreEqual(expectedIssuer, tokenPayload.iss);
    }

    [TestMethod]
    public async Task Tenant1User_CannotAccessTenant2Data()
    {
        // Arrange: Create 2 tenants with users
        // Act: Query with Tenant1 context
        // Assert: Only Tenant1 data returned
    }
}
```

### Data Isolation Test Template
```csharp
[TestMethod]
public async Task ServiceQuery_FiltersByTenantId()
{
    // Arrange
    var tenant1 = CreateTenant("tenant1");
    var tenant2 = CreateTenant("tenant2");
    var user1 = CreateUser(tenant1.Id);
    var user2 = CreateUser(tenant2.Id);

    // Act
    var tenant1Users = await _userService.GetUsersAsync(tenant1.Id);

    // Assert
    Assert.AreEqual(1, tenant1Users.Count);
    Assert.IsTrue(tenant1Users.All(u => u.TenantId == tenant1.Id));
}
```

---

## 📋 Daily Checklist

### Monday Oct 7
- [ ] Create E2E test project structure
- [ ] Write tenant creation E2E test
- [ ] Write token issuance E2E test
- [ ] Verify issuer format per tenant

### Tuesday Oct 8
- [ ] Write cross-tenant isolation tests
- [ ] Test User/Client/Consent isolation
- [ ] Verify JWKS tenant filtering
- [ ] Test discovery per tenant

### Wednesday Oct 9
- [ ] Write mode switching tests
- [ ] Test single-tenant mode (root issuer)
- [ ] Test multi-tenant mode (path issuer)
- [ ] Test fallback routes

### Thursday Oct 10
- [ ] Write security tests
- [ ] Test platform admin authorization
- [ ] Test impersonation security
- [ ] Test user portal authorization

### Friday Oct 11
- [ ] Write documentation
- [ ] Integration testing guide
- [ ] Mode switching procedure
- [ ] Security audit summary
- [ ] Phase 1 complete! 🎉

---

## 🎯 Success Metrics

### Phase 1 Exit Criteria
- [ ] 331+ unit tests passing
- [ ] 50+ integration/E2E tests passing
- [ ] Zero cross-tenant data leaks
- [ ] Mode switching documented
- [ ] Performance < 200ms token issuance

### Quality Standards
- Code coverage > 80%
- No critical security findings
- All docs up to date
- All TODOs addressed

---

## 🆘 Troubleshooting

### Test Failures
```powershell
# Clean and rebuild
dotnet clean
dotnet build

# Clear test cache
rm -r TestResults/

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"
```

### Database Issues
```powershell
# Reset database (Docker)
docker-compose down -v
docker-compose up -d

# Apply migrations
dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth
```

### Tenant Resolution Issues
- Check `MultiTenancy:Enabled` in appsettings
- Verify tenant slug in URL: `/t/{slug}/...`
- Check middleware order in `Program.cs`
- Ensure tenant exists in database

---

## 📞 Need Help?

**Documentation:**
- [Main Backlog](multitenancy-backlog.md) - Full implementation plan
- [Phase 1 Next Steps](phase1-complete-next-steps.md) - This week's plan
- [Roadmap](multitenancy-roadmap-october-2025.md) - Big picture

**Code Locations:**
- Platform Admin: `Pages/PlatformAdmin/`
- User Portal: `Pages/Account/`
- Multi-Tenancy Core: `MrWhoOidc.Auth/MultiTenancy/`

**Common Questions:**
- How to create a tenant? → Platform Admin UI (`/PlatformAdmin/Tenants/Create`)
- How to test multi-tenant? → Use tenant-prefixed URLs (`/t/{slug}/...`)
- How to switch modes? → Set `MultiTenancy:Enabled` in appsettings
- Where are tests? → `MrWhoOidc.UnitTests/`

---

## 🎉 Phase 1 Highlights

**What We Built:**
- 🏗️ Complete multi-tenancy infrastructure
- 👨‍💼 Platform Admin UI (tenant management)
- 👤 User Self-Service Portal (8 pages)
- 🔐 Authorization policies (platform/tenant/user)
- 🧪 331 passing unit tests
- 📚 Comprehensive documentation

**Lines of Code:**
- Core: ~3,000 lines
- UI: ~2,500 lines
- Tests: ~1,500 lines
- Docs: ~5,000 lines
- **Total: ~12,000 lines**

**Time Investment:**
- Development: ~70 hours
- Testing: ~10 hours
- Documentation: ~5 hours
- **Total: ~85 hours**

---

**Keep Going! Phase 1 is Almost Done! 💪**

Last Updated: October 7, 2025
