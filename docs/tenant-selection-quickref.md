# Tenant Selection Login - Quick Reference

**Status:** Proposal  
**Implementation:** 4-week plan  
**Priority:** HIGH

---

## The Problem

Users can't log in without knowing tenant-specific URL: `/t/{slug}/login`

---

## The Solution

Email-first login flow with automatic tenant discovery:

1. User enters email at root `/login`
2. System finds all tenants with that email
3. Auto-redirect (1 tenant) or show selection page (2+ tenants)
4. User completes login at tenant-specific page

---

## Architecture Decision: Option 1 (No Schema Changes)

**Why:**
- ✅ Fastest to implement (no migrations)
- ✅ Works with existing schema
- ✅ Backward compatible
- ✅ Low risk

**How it works:**
```sql
-- Find tenants by email
SELECT DISTINCT t."Id", t."Slug", t."Name", t."LogoUrl"
FROM "Tenants" t
JOIN "Users" u ON u."TenantId" = t."Id"
WHERE u."NormalizedEmail" = @email
  AND t."Status" = 1

UNION

-- Include alternative emails
SELECT DISTINCT t."Id", t."Slug", t."Name", t."LogoUrl"
FROM "Tenants" t
JOIN "Users" u ON u."TenantId" = t."Id"
JOIN "UserAlternativeEmails" uae ON uae."UserId" = u."Id"
WHERE uae."NormalizedEmail" = @email
  AND uae."IsVerified" = true
  AND t."Status" = 1;
```

---

## User Flows

### Flow 1: Single Tenant User
```
/login → Enter email → System finds 1 tenant 
       → Auto-redirect to /t/acme/login?email=admin@example.com
       → Enter password → Authenticated ✓
```

### Flow 2: Multi-Tenant User
```
/login → Enter email → System finds 3 tenants
       → Show selection page with tenant cards
       → User picks "Acme Corp"
       → Redirect to /t/acme/login?email=admin@example.com
       → Enter password → Authenticated ✓
```

### Flow 3: New User
```
/login → Enter email → System finds 0 tenants
       → Show "No account found"
       → Offer registration link
```

---

## Implementation Phases

### Week 1: Backend Service
- `ITenantDiscoveryService` interface
- Email-based tenant lookup query
- Rate limiting (5 req/min per IP)
- Audit logging

### Week 2: Email Input Page
- `/Pages/TenantDiscovery.cshtml`
- Email validation
- Redirect logic (0/1/2+ tenants)
- Error handling

### Week 3: Tenant Selection UI
- `/Pages/SelectTenant.cshtml`
- Tenant card component (logo, name, slug)
- Selection and redirect
- "Remember my choice" checkbox

### Week 4: Polish
- Email pre-fill on login
- Preferred tenant cookie
- Performance optimization
- Security audit

---

## Key Files to Create

```
MrWhoOidc.Auth/Services/
  └── ITenantDiscoveryService.cs
  └── TenantDiscoveryService.cs

MrWhoOidc.WebAuth/Pages/
  └── TenantDiscovery.cshtml
  └── TenantDiscovery.cshtml.cs
  └── SelectTenant.cshtml
  └── SelectTenant.cshtml.cs

MrWhoOidc.UnitTests/
  └── TenantDiscoveryServiceTests.cs
  └── TenantDiscoveryFlowTests.cs
```

---

## API Contract

```csharp
public interface ITenantDiscoveryService
{
    Task<List<TenantInfo>> FindTenantsByEmailAsync(
        string email, 
        CancellationToken ct = default);
}

public class TenantInfo
{
    public string Slug { get; set; }
    public string Name { get; set; }
    public string? LogoUrl { get; set; }
    public string LoginUrl { get; set; } // /t/{slug}/login
}
```

---

## Security Controls

1. **Rate Limiting:** 5 requests/minute per IP for email discovery
2. **Generic Responses:** Don't distinguish "no email" vs "no tenants"
3. **Audit Logging:** Log all discovery attempts with IP + timestamp
4. **CAPTCHA:** After 3 failed attempts (optional)
5. **Verified Emails Only:** Alternative emails must be verified

---

## UI Mockups

### Email Input (Step 1)
```
┌─────────────────────────────────────┐
│         [Company Logo]              │
│                                     │
│     Sign in to your account         │
│                                     │
│  ┌────────────────────────────────┐ │
│  │ Email address                  │ │
│  │ admin@example.com              │ │
│  └────────────────────────────────┘ │
│                                     │
│         [Continue →]                │
└─────────────────────────────────────┘
```

### Tenant Selection (Step 2)
```
┌─────────────────────────────────────┐
│    Select your organization         │
│    admin@example.com                │
│                                     │
│  ┌────────────────────────────────┐ │
│  │ [Logo] Acme Corp      [Select]│ │
│  └────────────────────────────────┘ │
│  ┌────────────────────────────────┐ │
│  │ [Logo] Globex Inc     [Select]│ │
│  └────────────────────────────────┘ │
│                                     │
│  ← Back                             │
└─────────────────────────────────────┘
```

---

## Metrics to Track

- **Login completion rate:** % successful logins
- **Time to login:** Average seconds from landing to authenticated
- **Support tickets:** Reduction in "forgot tenant URL" tickets
- **Discovery latency:** p95 response time < 200ms
- **Rate limit blocks:** % of malicious requests blocked

---

## Backward Compatibility

✅ Direct tenant URLs still work: `/t/{slug}/login`  
✅ Existing bookmarks not broken  
✅ API clients unaffected  
✅ Old flow works indefinitely

---

## Testing Checklist

- [ ] User with 1 tenant → auto-redirect works
- [ ] User with 2+ tenants → selection page shown
- [ ] User with 0 tenants → error message shown
- [ ] Alternative email lookup works
- [ ] Rate limiting triggers at 5 req/min
- [ ] Email enumeration protection effective
- [ ] Case-insensitive email matching
- [ ] Suspended tenants excluded from results
- [ ] Email pre-fill on tenant login page
- [ ] "Remember my choice" saves preference

---

## Rollout Plan

1. **Week 1:** Internal testing (staging)
2. **Week 2-3:** Beta users (10% of traffic)
3. **Week 4:** Gradual rollout (50% → 100%)
4. **Month 3:** Deprecate old flow with redirects

---

## Questions & Answers

**Q: What if email exists in suspended tenant?**  
A: Suspended tenants excluded from discovery results.

**Q: Should alternative emails be included?**  
A: Yes, but only if verified (`IsVerified = true`).

**Q: How to prevent email enumeration?**  
A: Rate limiting + generic responses + CAPTCHA (optional).

**Q: Can users switch tenants after login?**  
A: Future enhancement (Phase 5). Requires re-authentication.

**Q: What about platform admin accounts?**  
A: Show only tenant memberships, not all tenants.

---

## Related Documents

- Full backlog: `docs/tenant-selection-login-flow.md`
- Multi-tenancy guide: `docs/multitenancy-backlog.md`
- Tenant creation flow: `docs/tenant-creation-ui-flow.md`

---

## Next Steps

1. ✅ Review and approve proposal
2. ⏳ Create GitHub issues for Sprint 1
3. ⏳ Assign developers
4. ⏳ Begin implementation

**Target:** MVP in 4 weeks, full rollout in 6 weeks
