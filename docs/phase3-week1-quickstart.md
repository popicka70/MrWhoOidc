# Phase 3 Week 1: E2E Testing - Quick Start Guide

**Week:** October 14-18, 2025  
**Focus:** Comprehensive E2E test coverage for multi-tenant flows  
**Goal:** 30+ new tests, zero cross-tenant leaks detected

---

## 📋 Day-by-Day Tasks

### Day 1 (Oct 14): Multi-Tenant Token Flow Tests - Setup

**Morning (2-3 hours):**
1. Create new test file: `MrWhoOidc.UnitTests/E2E/MultiTenantTokenFlowTests.cs`
2. Set up test helpers:
   ```csharp
   private async Task<Tenant> CreateTestTenantAsync(string slug, string name)
   private async Task<Client> CreateTestClientAsync(Guid tenantId, string clientId)
   private async Task<string> IssueAuthorizationCodeAsync(Client client, User user)
   private async Task<TokenResponse> ExchangeCodeForTokenAsync(string code, Client client)
   ```

**Afternoon (3-4 hours):**
3. Write first 3 tests:
   - `TwoTenants_DifferentIssuers_TokensIssueSuccessfully()`
   - `Token_FromTenantA_RejectedByTenantB_IssuerMismatch()`
   - `JWKS_Endpoint_ReturnsOnlyTenantKeys()`

**End of Day:**
- 3 tests written and passing
- Test helpers implemented

---

### Day 2 (Oct 15): Multi-Tenant Token Flow Tests - Complete

**Morning (2-3 hours):**
1. Write discovery endpoint tests:
   - `Discovery_Endpoint_ReturnsTenantSpecificIssuer()`
   - `Discovery_Endpoint_TenantA_DiffersFromTenantB()`

2. Write client credentials flow tests:
   - `M2M_Client_IssuesToken_WithCorrectIssuer()`
   - `M2M_Token_FromTenantA_FailsIntrospectionByTenantB()`

**Afternoon (3-4 hours):**
3. Write refresh token tests:
   - `RefreshToken_IssuedByTenantA_OnlyWorksInTenantA()`
   - `RefreshToken_CrossTenantExchange_Fails()`

4. Write token introspection tests:
   - `Introspection_Token_OnlyWorksInIssuingTenant()`
   - `Introspection_CrossTenant_ReturnsInactive()`

**End of Day:**
- 10 total tests (Day 1 + Day 2)
- All tests passing
- Token flow isolation verified

---

### Day 3 (Oct 16): Data Isolation Verification - Part 1

**Morning (2-3 hours):**
1. Create new test file: `MrWhoOidc.UnitTests/E2E/DataIsolationTests.cs`
2. Write user isolation tests:
   - `User_InTenantA_NotVisibleToTenantB()`
   - `User_Email_UniquePerTenant_NotGlobal()`
   - `UserService_GetByEmail_OnlyReturnsTenantUsers()`

**Afternoon (3-4 hours):**
3. Write client isolation tests:
   - `Client_InTenantA_NotVisibleToTenantB()`
   - `ClientStore_GetByClientId_OnlyReturnsTenantClients()`
   - `Client_SameClientId_DifferentTenants_Allowed()`

**End of Day:**
- 6 data isolation tests written and passing
- User and client isolation verified

---

### Day 4 (Oct 17): Data Isolation Verification - Part 2

**Morning (2-3 hours):**
1. Write consent isolation tests:
   - `Consent_InTenantA_NotVisibleToTenantB()`
   - `ConsentService_GetUserConsents_OnlyReturnsTenantData()`

2. Write session isolation tests:
   - `Session_InTenantA_NotVisibleToTenantB()`
   - `Session_Revocation_OnlyAffectsTenantSessions()`

**Afternoon (3-4 hours):**
3. Write token isolation tests:
   - `Token_IssuedByTenantA_NotFoundByTenantB()`
   - `RefreshTokenService_GetByToken_OnlyReturnsTenantTokens()`

4. Write authorization code isolation tests:
   - `AuthCode_IssuedByTenantA_NotFoundByTenantB()`
   - `AuthCodeService_Exchange_OnlyWorksSameTenant()`

**End of Day:**
- 14 total data isolation tests (Day 3 + Day 4)
- All tests passing
- Comprehensive isolation verified

---

### Day 5 (Oct 18): Mode Switching & Settings E2E Tests

**Morning (2-3 hours):**
1. Create new test file: `MrWhoOidc.UnitTests/E2E/ModeSwitchingTests.cs`
2. Write mode switching tests:
   - `SingleTenantMode_UsesRootIssuer_NoTenantPrefix()`
   - `MultiTenantMode_UsesPathBasedIssuer_WithTenantPrefix()`
   - `FallbackRoutes_NonPrefixed_MapToDefaultTenant()`
   - `Discovery_Endpoint_ReflectsCorrectMode()`

**Afternoon (3-4 hours):**
3. Create new test file: `MrWhoOidc.UnitTests/E2E/SettingsOverrideE2ETests.cs`
4. Write settings override tests:
   - `TokenLifetime_Override_ReflectedInActualToken()`
   - `PasswordPolicy_TenantOverride_EnforcedOnRegistration()`
   - `MFA_Required_Setting_EnforcedOnLogin()`
   - `PKCE_Required_Setting_EnforcedOnAuthRequest()`

**End of Day:**
- 8 additional tests (mode + settings)
- **Total Week 1: 32 new tests (10 token + 14 isolation + 4 mode + 4 settings)**
- Compile test results report
- Document any issues found
- Create Week 2 task list

---

## 🎯 Week 1 Success Criteria

- [ ] 30+ new E2E tests written and passing
- [ ] Zero cross-tenant data leaks detected
- [ ] Token flow isolation verified (issuer, JWKS, introspection)
- [ ] Data isolation verified (users, clients, consents, sessions, tokens)
- [ ] Mode switching tested (single ↔ multi-tenant)
- [ ] Settings override verified end-to-end
- [ ] Test results report compiled

**Target Test Count:** 398 total (366 current + 32 new)

---

## 🔧 Test Infrastructure Setup

### Prerequisites
- Existing test infrastructure from Phase 1/2
- `TestDataSeeder.cs` for creating test data
- `MockTenantSettingsService.cs` for settings mocks
- In-memory EF Core database for tests

### New Test Helpers to Create

**TenantTestHelper.cs** (for creating test tenants):
```csharp
public static class TenantTestHelper
{
    public static Tenant CreateTenant(string slug, string name)
    {
        return new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = name,
            IssuerUri = $"https://localhost:5001/t/{slug}",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
    
    public static async Task<Tenant> SeedTenantAsync(AuthDbContext db, string slug, string name)
    {
        var tenant = CreateTenant(slug, name);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }
}
```

**TokenFlowTestHelper.cs** (for token flow testing):
```csharp
public static class TokenFlowTestHelper
{
    public static async Task<string> IssueAuthorizationCodeAsync(
        IAuthorizationCodeService codeService,
        Client client,
        User user,
        string redirectUri,
        string scope)
    {
        var code = await codeService.CreateAuthorizationCodeAsync(
            clientId: client.ClientId,
            userId: user.Id,
            redirectUri: redirectUri,
            scope: scope,
            codeChallenge: null,
            codeChallengeMethod: null,
            nonce: null,
            realmId: user.RealmId
        );
        return code;
    }
    
    public static async Task<TokenResponse> ExchangeCodeForTokenAsync(
        ITokenService tokenService,
        string code,
        Client client,
        string redirectUri)
    {
        var response = await tokenService.ExchangeAuthorizationCodeAsync(
            code: code,
            clientId: client.ClientId,
            clientSecret: client.ClientSecret,
            redirectUri: redirectUri,
            codeVerifier: null
        );
        return response;
    }
}
```

---

## 📊 Test Coverage Matrix

| Category | Test Count | Status |
|----------|-----------|--------|
| **Token Flow** | 10 | 📋 To Do |
| - Issuer verification | 2 | |
| - Cross-tenant rejection | 2 | |
| - JWKS isolation | 2 | |
| - Client credentials | 2 | |
| - Refresh tokens | 2 | |
| **Data Isolation** | 14 | 📋 To Do |
| - User isolation | 3 | |
| - Client isolation | 3 | |
| - Consent isolation | 2 | |
| - Session isolation | 2 | |
| - Token isolation | 2 | |
| - Auth code isolation | 2 | |
| **Mode Switching** | 4 | 📋 To Do |
| - Single-tenant mode | 1 | |
| - Multi-tenant mode | 1 | |
| - Fallback routes | 1 | |
| - Discovery endpoint | 1 | |
| **Settings Override** | 4 | 📋 To Do |
| - Token lifetime | 1 | |
| - Password policy | 1 | |
| - MFA enforcement | 1 | |
| - PKCE requirement | 1 | |
| **Total Week 1** | **32** | 📋 To Do |

---

## 🚨 Common Pitfalls to Avoid

1. **Tenant Context Not Set**
   - Always mock `ITenantAccessor.CurrentTenant` before calling services
   - Verify tenant context in test setup

2. **Database State Not Isolated**
   - Use separate in-memory database per test
   - Clean up data between tests

3. **Hardcoded Tenant IDs**
   - Use Guid.NewGuid() or well-known GUIDs (e.g., default tenant)
   - Document any hardcoded IDs

4. **Async Test Pitfalls**
   - Always await async calls
   - Use `.Result` only in synchronous helpers (not in test methods)

5. **Issuer Validation Skipped**
   - Verify actual issuer string matches expected tenant issuer
   - Don't just check token exists; validate claims

---

## 📖 Reference Documentation

- **Test Patterns:** `MrWhoOidc.UnitTests/MultiTenancy/MultiTenantE2ETests.cs` (existing example)
- **Test Helpers:** `MrWhoOidc.UnitTests/Helpers/TestDataSeeder.cs`
- **Mock Services:** `MrWhoOidc.UnitTests/Helpers/MockTenantSettingsService.cs`
- **Phase 3 Plan:** `docs/phase3-next-steps-summary.md`
- **Full Backlog:** `docs/multitenancy-backlog.md` (Section 22)

---

## ✅ Daily Checklist Template

**Start of Day:**
- [ ] Pull latest code from master
- [ ] Run all existing tests (verify 366 passing)
- [ ] Review day's tasks

**During Development:**
- [ ] Write tests using AAA pattern (Arrange, Act, Assert)
- [ ] Run tests locally (verify passing)
- [ ] Commit tests with descriptive messages

**End of Day:**
- [ ] Run all tests (old + new)
- [ ] Document any issues found
- [ ] Update daily progress in Phase 3 summary
- [ ] Push code to branch

---

## 🎉 Week 1 Completion Criteria

**When to mark Week 1 complete:**
- ✅ All 32 planned tests written and passing
- ✅ Total test count: 398+ (366 existing + 32 new)
- ✅ Test results report compiled
- ✅ Zero cross-tenant data leaks detected
- ✅ Code reviewed and merged to master
- ✅ Week 2 task list created

**Week 1 Deliverables:**
- `MultiTenantTokenFlowTests.cs` (10 tests)
- `DataIsolationTests.cs` (14 tests)
- `ModeSwitchingTests.cs` (4 tests)
- `SettingsOverrideE2ETests.cs` (4 tests)
- `TenantTestHelper.cs` (test helper)
- `TokenFlowTestHelper.cs` (test helper)
- Week 1 test results report (Markdown doc)

---

**Ready to Start?** Begin with Day 1 tasks (Multi-Tenant Token Flow Tests - Setup).

**Questions?** Refer to existing test patterns in `MultiTenantE2ETests.cs` or ask for clarification.
