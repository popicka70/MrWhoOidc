# Tenant Selection Login Flow - Implementation Backlog

**Status:** Proposal  
**Priority:** HIGH (Critical UX Issue)  
**Created:** October 5, 2025  
**Objective:** Allow users to log in using their email address without needing to remember tenant-specific URLs

---

## Problem Statement

Currently, users must know and navigate to tenant-specific URLs to log in:
```
https://localhost:8443/t/{tenant-slug}/login
```

This creates several UX problems:
1. **Discoverability:** Users don't know which tenant slug to use
2. **Memory burden:** Users must remember tenant slugs
3. **Error-prone:** Typos in tenant slugs lead to 404 errors
4. **Poor first-time experience:** New users have no way to discover their tenant
5. **Support burden:** Help desk must provide tenant-specific URLs

### Current State Analysis

**Schema Facts:**
- `User.Email` - unique per tenant (composite index: `TenantId, NormalizedEmail`)
- `User.Username` - unique per tenant (composite index: `TenantId, Username`)
- `UserAlternativeEmail.Email` - globally unique (unique index on `NormalizedEmail`)
- **Email can span tenants** (same email can exist in multiple tenants)
- **Username is tenant-scoped** (different meaning per tenant)

**Key Insight:** Email is the natural cross-tenant identifier, as it represents a real-world identity.

---

## Solution Options

### Option 1: Tenant Discovery Landing Page ⭐ **RECOMMENDED**

**Flow:**
1. User navigates to root login: `https://localhost:8443/login`
2. Page shows single field: "Email address"
3. User enters email (e.g., `admin@example.com`)
4. System queries database for all tenants where this email exists
5. If **one tenant** → auto-redirect to `/t/{slug}/login?email=admin@example.com`
6. If **multiple tenants** → show tenant selection page
7. User selects tenant → redirect to tenant-specific login

**Pros:**
✅ Simple, intuitive UX (email-first approach)  
✅ Works with existing schema (no migration needed)  
✅ Handles single-tenant and multi-tenant scenarios gracefully  
✅ Backward compatible (existing `/t/{slug}/login` still works)  
✅ Supports users with accounts in multiple tenants  
✅ Can show tenant branding (logo, name) to help user identify

**Cons:**
⚠️ Email enumeration possible (mitigated with rate limiting)  
⚠️ Extra step for users with one tenant (can be cached/remembered)  
⚠️ Requires new UI component (tenant selector)

**Security Considerations:**
- Rate limit email lookups (e.g., 5 requests per minute per IP)
- Generic responses to prevent email enumeration attacks
- Audit logging of tenant discovery attempts
- Optional: require email verification code before showing tenants

---

### Option 2: Universal Email-Based Login

**Flow:**
1. User navigates to `https://localhost:8443/login`
2. User enters email + password
3. System finds matching user(s) across all tenants
4. If **one match** → authenticate and set tenant context
5. If **multiple matches** → show tenant selection after password verification
6. Set cookie with tenant context for subsequent requests

**Pros:**
✅ Streamlined UX (fewer steps)  
✅ Familiar login flow (email + password on one page)  
✅ Works well for users with single tenant access

**Cons:**
❌ Password checked before tenant selection (privacy concern)  
❌ More complex authentication logic  
❌ May leak information through timing attacks  
❌ Difficult to handle tenant-specific password policies

---

### Option 3: Email-to-Tenant Mapping Table (Global Identity)

**Schema Change:**
```csharp
public class GlobalIdentity
{
    public Guid Id { get; set; }
    public string Email { get; set; } // Globally unique
    public string NormalizedEmail { get; set; }
    public bool EmailVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class GlobalIdentityTenantMembership
{
    public Guid GlobalIdentityId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; } // Foreign key to User
    public DateTimeOffset JoinedAt { get; set; }
    public bool IsActive { get; set; }
}
```

**Flow:**
1. User logs in with email
2. System looks up GlobalIdentity by email
3. Shows list of tenants this identity has access to
4. User selects tenant → redirects to tenant-specific login

**Pros:**
✅ Clean separation of identity vs. tenant membership  
✅ Supports SSO across tenants  
✅ Foundation for future features (tenant switching, unified profile)  
✅ Email uniqueness enforced at global level

**Cons:**
❌ Requires significant schema changes and migration  
❌ Complex refactoring of existing authentication logic  
❌ Breaking change for existing deployments  
❌ Harder to implement federated identity providers per-tenant

---

### Option 4: Subdomain-Based Tenant Routing

**Architecture:**
```
tenant1.mrwho.example.com/login
tenant2.mrwho.example.com/login
```

**Flow:**
1. User navigates to root domain: `mrwho.example.com`
2. Landing page: "Enter your email to find your organization"
3. System looks up tenant by email
4. Redirects to: `{tenant-slug}.mrwho.example.com/login`

**Pros:**
✅ Industry-standard approach (Slack, GitHub, etc.)  
✅ Clean URLs without path prefixes  
✅ Each tenant feels like separate application  
✅ Easier SSL/TLS certificate management per tenant

**Cons:**
❌ Requires DNS configuration and wildcard SSL certificates  
❌ More complex deployment and infrastructure  
❌ Harder to develop/test locally  
❌ May not work in all network environments (VPN, firewalls)

---

## Recommended Implementation: Option 1 (Phased Approach)

### Phase 1: Tenant Discovery Service (Foundation)

**Goal:** Create backend service to query tenants by email

**Tasks:**
1. ✅ Create `ITenantDiscoveryService` interface
2. ✅ Implement email-based tenant lookup
3. ✅ Query both `User.Email` and `UserAlternativeEmail.Email`
4. ✅ Return tenant metadata (slug, name, logo, status)
5. ✅ Add rate limiting middleware for discovery endpoint
6. ✅ Add audit logging

**Deliverable:** Service that can answer "Which tenants have this email?"

---

### Phase 2: Root Login Landing Page

**Goal:** Create email-first login experience at `/login`

**UI Mockup:**
```
┌─────────────────────────────────────────┐
│                                         │
│          [Company Logo]                 │
│                                         │
│     Sign in to your account             │
│                                         │
│  ┌────────────────────────────────────┐ │
│  │ Email address                      │ │
│  │ admin@example.com                  │ │
│  └────────────────────────────────────┘ │
│                                         │
│           [Continue →]                  │
│                                         │
│  Don't have an account? Register       │
│                                         │
└─────────────────────────────────────────┘
```

**Tasks:**
1. ✅ Create new Razor Page: `/Pages/TenantDiscovery.cshtml`
2. ✅ Update root `/login` route to show email input first
3. ✅ On submit: call `ITenantDiscoveryService`
4. ✅ Store email in TempData for next step
5. ✅ Redirect logic:
   - 0 tenants → Show error "No account found with this email"
   - 1 tenant → Auto-redirect to `/t/{slug}/login?email={email}`
   - 2+ tenants → Redirect to `/select-tenant` page

**Deliverable:** Email-first login experience

---

### Phase 3: Tenant Selection Page

**Goal:** Allow user to choose between multiple tenant memberships

**UI Mockup:**
```
┌─────────────────────────────────────────┐
│                                         │
│      Select your organization           │
│      admin@example.com                  │
│                                         │
│  ┌────────────────────────────────────┐ │
│  │  [Logo] Acme Corp                  │ │
│  │         acme-corp.com              │ │
│  │                        [Continue →]│ │
│  └────────────────────────────────────┘ │
│                                         │
│  ┌────────────────────────────────────┐ │
│  │  [Logo] Globex Inc                 │ │
│  │         globex.com                 │ │
│  │                        [Continue →]│ │
│  └────────────────────────────────────┘ │
│                                         │
│  ← Back to email entry                  │
│                                         │
└─────────────────────────────────────────┘
```

**Tasks:**
1. ✅ Create `/Pages/SelectTenant.cshtml`
2. ✅ Display tenant cards with:
   - Tenant logo (if configured)
   - Tenant name
   - Tenant slug (subdomain/path)
   - Last login timestamp (if available)
3. ✅ On selection: redirect to `/t/{slug}/login?email={email}`
4. ✅ Optionally: show "Remember my choice" checkbox
5. ✅ Store preference in cookie for next visit

**Deliverable:** Multi-tenant selection UI

---

### Phase 4: Email Pre-fill and Remember Me

**Goal:** Streamline return visits

**Tasks:**
1. ✅ Pre-fill email on tenant-specific login page
2. ✅ Add "Remember this organization" option
3. ✅ Store cookie: `preferred_tenant_{email_hash} = {tenant_slug}`
4. ✅ On return visit to root `/login`:
   - Check if email has preferred tenant
   - Auto-redirect to that tenant (show "Signing in to Acme Corp..." spinner)
   - Provide "Not you? Use different organization" link

**Deliverable:** Improved UX for repeat visitors

---

### Phase 5: Tenant Switching (Future Enhancement)

**Goal:** Allow authenticated users to switch tenants without re-login

**Requirements:**
- User authenticated in Tenant A
- User also has account in Tenant B with same email
- User clicks "Switch Organization" in UI

**Challenges:**
- Password may differ between tenants
- Roles/permissions differ per tenant
- Session/token management complexity

**Approach:**
- Require re-authentication when switching
- Or: implement federated identity (Option 3) later

---

## Security & Privacy Considerations

### Email Enumeration Protection

**Problem:** Attackers can discover which emails have accounts

**Mitigations:**
1. **Rate Limiting:**
   ```csharp
   [RateLimit(Policy = "email-discovery", Requests = 5, Window = "1m")]
   public async Task<IActionResult> DiscoverTenants(string email)
   ```

2. **Generic Responses:**
   - Don't distinguish between "email not found" vs "no tenants"
   - Always show same response time (add artificial delay if needed)

3. **CAPTCHA/Proof-of-Work:**
   - Require CAPTCHA after 3 failed attempts
   - Implement proof-of-work challenge for automated requests

4. **Audit Logging:**
   - Log all discovery attempts with IP, timestamp
   - Alert on suspicious patterns (many emails from one IP)

5. **Email Verification (Optional):**
   - Send verification code to email before showing tenants
   - Only show tenants after code confirmation

### Alternative Emails Handling

**Current State:**
- `UserAlternativeEmail` has globally unique constraint
- User can have multiple alternative emails

**Considerations:**
1. **Should alternative emails be included in tenant discovery?**
   - **YES:** Better UX, users can use any verified email
   - **NO:** Security concern, alternative emails may be less trusted

**Recommendation:** Include verified alternative emails in discovery

### Cross-Tenant Data Leakage

**Risk:** User in Tenant A discovers they have account in Tenant B

**Mitigation:**
- This is expected behavior (user knows their own email)
- Only show tenant name/logo, no sensitive data
- User must still authenticate to access tenant
- Audit log shows which tenants were discovered by whom

---

## Database Schema Changes

### Phase 1: No Schema Changes Required ✅

Current schema supports email-based tenant lookup:
```sql
-- Find all tenants for an email
SELECT DISTINCT t."Id", t."Slug", t."Name", t."LogoUrl"
FROM "Tenants" t
JOIN "Users" u ON u."TenantId" = t."Id"
WHERE u."NormalizedEmail" = @email
  AND t."Status" = 1; -- Active

-- Include alternative emails
UNION

SELECT DISTINCT t."Id", t."Slug", t."Name", t."LogoUrl"
FROM "Tenants" t
JOIN "Users" u ON u."TenantId" = t."Id"
JOIN "UserAlternativeEmails" uae ON uae."UserId" = u."Id"
WHERE uae."NormalizedEmail" = @email
  AND uae."IsVerified" = true
  AND t."Status" = 1;
```

### Phase 4: Optional Preference Storage

**Option A:** Use cookies (no schema change)
```csharp
Response.Cookies.Append($"preferred_tenant", tenantSlug, new CookieOptions
{
    Expires = DateTimeOffset.UtcNow.AddDays(90),
    HttpOnly = true,
    Secure = true,
    SameSite = SameSiteMode.Lax
});
```

**Option B:** Database table (more reliable)
```csharp
public class UserTenantPreference
{
    public Guid Id { get; set; }
    public string NormalizedEmail { get; set; } // Not FK, just lookup key
    public Guid TenantId { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public string? IpAddress { get; set; } // Optional: location-aware preferences
}
```

---

## API Design

### Tenant Discovery Endpoint

```csharp
// POST /api/discover-tenant
public class DiscoverTenantRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; }
}

public class DiscoverTenantResponse
{
    public bool Success { get; set; }
    public List<TenantInfo> Tenants { get; set; }
    public string? Message { get; set; }
}

public class TenantInfo
{
    public string Slug { get; set; }
    public string Name { get; set; }
    public string? LogoUrl { get; set; }
    public string LoginUrl { get; set; } // /t/{slug}/login
    public DateTimeOffset? LastLoginAt { get; set; } // If user has logged in before
}
```

### Service Interface

```csharp
public interface ITenantDiscoveryService
{
    /// <summary>
    /// Find all active tenants where the given email has a user account.
    /// Searches both primary email and verified alternative emails.
    /// </summary>
    Task<List<TenantInfo>> FindTenantsByEmailAsync(
        string email, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Get user's preferred tenant based on email and cookies/IP.
    /// Returns null if no preference found.
    /// </summary>
    Task<TenantInfo?> GetPreferredTenantAsync(
        string email, 
        string? ipAddress = null, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Save user's tenant preference for faster login next time.
    /// </summary>
    Task SaveTenantPreferenceAsync(
        string email, 
        Guid tenantId, 
        string? ipAddress = null, 
        CancellationToken ct = default);
}
```

---

## UI/UX Flow Diagrams

### Current Flow (Problem)
```
User
  │
  ├─ Knows tenant slug? ────────────┐
  │                                 │
  NO                               YES
  │                                 │
  ├─ Contacts support               ├─ Goes to /t/{slug}/login
  ├─ Gets tenant URL                ├─ Enters credentials
  ├─ Goes to URL                    ├─ Authenticated ✓
  ├─ Enters credentials             │
  └─ Authenticated ✓                │
```

### Proposed Flow (Solution)
```
User
  │
  ├─ Goes to /login (root)
  ├─ Enters email
  │
  ├─ 0 tenants found ────────┐
  │                          │
  │                   Show "No account found"
  │                   Offer registration link
  │
  ├─ 1 tenant found ─────────┤
  │                          │
  │                   Auto-redirect to /t/{slug}/login
  │                   Email pre-filled
  │                   Enter password
  │                   Authenticated ✓
  │
  └─ 2+ tenants found ───────┤
                             │
                      Show tenant selection page
                      User picks tenant
                      Redirect to /t/{slug}/login
                      Email pre-filled
                      Enter password
                      Authenticated ✓
```

---

## Implementation Priority & Timeline

### Sprint 1 (Week 1): Foundation
- [ ] Create `ITenantDiscoveryService` interface and implementation
- [ ] Add SQL query for email-based tenant lookup
- [ ] Write unit tests for tenant discovery logic
- [ ] Add rate limiting policy for discovery endpoint
- [ ] Add audit logging

**Deliverable:** Backend service ready for UI integration

### Sprint 2 (Week 2): Email-First Login
- [ ] Create `/Pages/TenantDiscovery.cshtml` (email input page)
- [ ] Update root `/login` route to show email step
- [ ] Implement redirect logic based on tenant count
- [ ] Add client-side email validation
- [ ] Add error handling and user feedback

**Deliverable:** Email-first login flow working for single-tenant users

### Sprint 3 (Week 3): Multi-Tenant Selection
- [ ] Create `/Pages/SelectTenant.cshtml`
- [ ] Design tenant card UI component
- [ ] Implement tenant selection and redirect
- [ ] Add "Remember my choice" checkbox
- [ ] Test with multiple tenants per email

**Deliverable:** Complete tenant selection experience

### Sprint 4 (Week 4): Polish & Optimization
- [ ] Implement preferred tenant cookie logic
- [ ] Add email pre-fill on tenant-specific login
- [ ] Add "Not you? Switch organization" link
- [ ] Performance optimization (caching, query tuning)
- [ ] Security audit and penetration testing
- [ ] Documentation and help text

**Deliverable:** Production-ready tenant discovery flow

---

## Testing Strategy

### Unit Tests
```csharp
[TestClass]
public class TenantDiscoveryServiceTests
{
    [TestMethod]
    public async Task FindTenantsByEmail_SingleTenant_ReturnsOne()
    
    [TestMethod]
    public async Task FindTenantsByEmail_MultipleTenants_ReturnsAll()
    
    [TestMethod]
    public async Task FindTenantsByEmail_IncludesAlternativeEmails()
    
    [TestMethod]
    public async Task FindTenantsByEmail_OnlyActiveTenantsReturned()
    
    [TestMethod]
    public async Task FindTenantsByEmail_CaseInsensitiveSearch()
}
```

### Integration Tests
```csharp
[TestClass]
public class TenantDiscoveryFlowTests
{
    [TestMethod]
    public async Task EmailDiscovery_OneTenant_AutoRedirects()
    
    [TestMethod]
    public async Task EmailDiscovery_MultipleTenants_ShowsSelection()
    
    [TestMethod]
    public async Task EmailDiscovery_NoTenants_ShowsError()
    
    [TestMethod]
    public async Task RateLimit_ExceedingLimit_Returns429()
}
```

### Manual Test Scenarios
1. User with account in one tenant
2. User with accounts in multiple tenants
3. User with no accounts (new user)
4. User with alternative email verified
5. User with preferred tenant cookie set
6. Rate limiting triggers correctly
7. Email enumeration protection works

---

## Backward Compatibility

### Existing Flows Preserved
✅ Direct tenant URLs still work: `/t/{slug}/login`  
✅ Existing bookmarks/links not broken  
✅ API clients using tenant-specific endpoints unaffected  
✅ Federated login flows (OIDC, SAML) still work per-tenant

### Migration Path for Existing Users
- No data migration needed (schema unchanged)
- Existing users automatically benefit from new flow
- Old login URLs continue to work indefinitely
- Can deprecate old URLs gradually with redirects

---

## Future Enhancements (Beyond Initial Implementation)

### 1. Federated Discovery
Allow external IdPs (Google, Microsoft) to integrate with tenant discovery:
```
User → Google Login → Extracts email → Tenant Discovery → Redirects
```

### 2. Tenant Switching Without Re-Auth
Once authenticated, allow switching between tenants with same email identity

### 3. Cross-Tenant SSO
Implement token exchange to enable SSO across tenants for same user

### 4. Organization Invites
Allow tenant admins to invite users by email, pre-registering them in discovery system

### 5. Mobile App Deep Linking
Generate deep links that open mobile app to specific tenant:
```
mrwho://login?tenant=acme-corp&email=user@example.com
```

### 6. Smart Tenant Suggestions
Use ML to suggest most likely tenant based on:
- Time of day
- IP location
- Device fingerprint
- Usage patterns

---

## Alternatives Considered (Not Recommended)

### ❌ Username-Based Discovery
- Usernames are tenant-scoped, not globally unique
- Same username can mean different people in different tenants
- Email is the natural cross-tenant identifier

### ❌ Phone Number-Based Discovery
- Not all users have phone numbers
- Phone numbers can change
- Email is more stable identifier

### ❌ Magic Link Login
- Requires email delivery infrastructure
- Slower UX (wait for email)
- Doesn't solve tenant discovery problem

---

## Success Metrics

### UX Metrics
- **Login completion rate:** % of users who successfully log in
- **Time to login:** Average time from landing page to authenticated state
- **Support ticket reduction:** Fewer "forgot tenant URL" tickets
- **Error rate:** % of login attempts that fail due to wrong tenant

### Technical Metrics
- **Discovery endpoint latency:** < 200ms p95
- **Rate limit effectiveness:** % of blocked malicious attempts
- **Cache hit rate:** % of tenant lookups served from cache

### Business Metrics
- **User satisfaction:** Survey scores for login experience
- **Adoption rate:** % of users using email-first flow vs direct URLs
- **Cost savings:** Reduced support costs

---

## Rollout Strategy

### Phase 1: Internal Testing (Week 1)
- Deploy to staging environment
- Test with internal team accounts
- Gather feedback and iterate

### Phase 2: Beta Users (Week 2-3)
- Enable for select beta tenants
- Monitor metrics and logs closely
- Fix bugs and polish UX

### Phase 3: Gradual Rollout (Week 4-6)
- Enable for 10% of users
- Increase to 50% after 1 week
- Full rollout after 2 weeks

### Phase 4: Deprecation of Old Flow (Month 3+)
- Add notices to old login URLs
- Redirect old URLs to new flow after 6 months
- Complete transition by end of quarter

---

## Documentation Requirements

1. **User Guide:**
   - "How to log in without knowing your organization URL"
   - "Managing multiple organization memberships"
   
2. **Admin Guide:**
   - "Tenant discovery and email-based login"
   - "Configuring tenant branding for selection page"
   
3. **Developer Guide:**
   - "Tenant discovery service API"
   - "Customizing tenant selection UI"
   
4. **Security Guide:**
   - "Email enumeration protection"
   - "Rate limiting configuration"

---

## Open Questions

1. **Should we allow users to create accounts during tenant discovery?**
   - Scenario: User enters email, no tenants found, offer "Create organization"?
   
2. **How to handle suspended/deleted tenants?**
   - Should they appear in discovery? With what status indicator?
   
3. **Should alternative emails be included in discovery by default?**
   - Security vs. UX tradeoff
   
4. **What to do with platform admin accounts?**
   - Should platform admins see all tenants or only their membership?
   
5. **Cookie vs. Database for preferences?**
   - Cookies: simpler, no schema change
   - Database: more reliable, works across devices

---

## Conclusion

**Recommended Approach:** Option 1 (Tenant Discovery Landing Page)

**Rationale:**
- ✅ No schema changes required (fastest implementation)
- ✅ Works with current architecture
- ✅ Solves the core UX problem
- ✅ Can be implemented incrementally
- ✅ Low risk, backward compatible
- ✅ Foundation for future enhancements

**Next Steps:**
1. Review and approve this backlog
2. Create Jira/GitHub issues for each sprint
3. Assign developers to Sprint 1 tasks
4. Begin implementation of `ITenantDiscoveryService`

**Estimated Timeline:** 4 weeks to MVP, 6 weeks to full rollout
