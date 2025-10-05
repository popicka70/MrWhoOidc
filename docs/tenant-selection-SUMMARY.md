# Tenant Selection Login - Executive Summary

**Document Set:** Proposal for email-first tenant discovery login flow  
**Created:** October 5, 2025  
**Status:** Ready for review and approval  
**Estimated Effort:** 4 weeks (MVP), 6 weeks (full rollout)

---

## The Problem in One Sentence

Users cannot log in without knowing the exact tenant-specific URL (`/t/{slug}/login`), creating a poor user experience and support burden.

---

## The Solution in One Sentence

Implement email-first login at root URL (`/login`) that automatically discovers and presents user's tenant(s), eliminating the need to remember tenant slugs.

---

## Why This Matters

### Current State (Bad UX)
```
User → Needs to know: https://localhost:8443/t/acme-corp/login
     → Contacts support
     → Gets URL
     → Finally logs in (poor experience)
```

### Proposed State (Good UX)
```
User → Goes to: https://localhost:8443/login
     → Enters: admin@example.com
     → System shows: "Acme Corp" or list of organizations
     → User logs in (smooth experience)
```

---

## How It Works

1. **User enters email** at `/login`
2. **System queries database** for tenants containing that email
3. **Three outcomes:**
   - **0 tenants:** Show "No account found" + registration link
   - **1 tenant:** Auto-redirect to that tenant's login
   - **2+ tenants:** Show selection page with tenant cards

---

## Key Architectural Decision

**Chosen Approach:** Option 1 - Tenant Discovery with Current Schema

**Why:**
- ✅ **No schema changes** (fastest implementation)
- ✅ **Backward compatible** (existing URLs still work)
- ✅ **Low risk** (doesn't break existing functionality)
- ✅ **Incremental** (can be deployed in phases)

**How:**
```sql
-- Simple query leveraging existing indexes
SELECT DISTINCT tenants
FROM Users 
WHERE NormalizedEmail = @email
UNION
SELECT DISTINCT tenants
FROM UserAlternativeEmails
WHERE NormalizedEmail = @email AND IsVerified = true
```

---

## Documents in This Package

### 1. **Full Backlog** (`tenant-selection-login-flow.md`)
- 📋 25KB comprehensive implementation plan
- Detailed analysis of 4 solution options
- Phased implementation roadmap (4 sprints)
- Security considerations and mitigations
- API design and service interfaces
- Testing strategy and success metrics
- Complete Q&A section

**Key Sections:**
- Problem statement and current state analysis
- Solution options comparison (4 alternatives)
- Recommended implementation (phased approach)
- Security & privacy considerations
- Database schema (no changes needed!)
- UI/UX flow diagrams
- Sprint-by-sprint task breakdown
- Testing checklist and rollout plan

### 2. **Quick Reference** (`tenant-selection-quickref.md`)
- 📝 8KB condensed reference guide
- One-page overview for developers
- Key implementation points
- Code snippets and API contracts
- Testing checklist
- FAQ section

**Best For:**
- Quick lookups during implementation
- Standup discussions
- Code review reference
- Onboarding new developers

### 3. **Visual Diagrams** (`tenant-selection-diagrams.md`)
- 🎨 24KB visual documentation
- Mermaid flow diagrams
- Sequence diagrams
- State machines
- UI mockups (ASCII art)
- Architecture diagrams
- Data flow visualization

**Best For:**
- Design reviews
- Architecture discussions
- Documentation in wikis/README
- Presentation slides

---

## Implementation Timeline

### Week 1: Backend Foundation
- Create `ITenantDiscoveryService`
- Implement email → tenant lookup
- Add rate limiting (5 req/min per IP)
- Add audit logging

### Week 2: Email Input Page
- Create root `/login` page (email-first)
- Implement redirect logic
- Error handling
- Client-side validation

### Week 3: Tenant Selection UI
- Create `/select-tenant` page
- Tenant card components
- Selection and redirect
- "Remember my choice" feature

### Week 4: Polish & Launch
- Email pre-fill optimization
- Preferred tenant cookies
- Performance tuning
- Security audit
- Beta testing → Production rollout

---

## Security Highlights

✅ **Rate Limiting:** 5 requests/minute per IP  
✅ **Generic Responses:** No email enumeration  
✅ **Audit Logging:** All discovery attempts logged  
✅ **Verified Emails Only:** Alternative emails must be verified  
✅ **CAPTCHA Ready:** Optional for suspicious patterns

**Attack Surface:**
- Email enumeration → Mitigated with rate limiting + generic responses
- Brute force → Existing login rate limits still apply
- Privacy leakage → Only shows tenant name, no sensitive data

---

## What Changes for Users

### Before (Current)
1. User must know tenant URL
2. Navigate to: `/t/acme-corp/login`
3. Enter: email + password
4. Authenticated ✓

### After (Proposed)
1. Navigate to: `/login` (easy to remember!)
2. Enter: email
3. System shows: "Acme Corp" (auto-detected)
4. Enter: password
5. Authenticated ✓

**Net Change:** +1 step but **eliminates the need to know tenant slug**

---

## What Doesn't Change

✅ Direct tenant URLs still work: `/t/{slug}/login`  
✅ API authentication unchanged  
✅ Federated login (OIDC/SAML) per-tenant still works  
✅ Existing user accounts need no migration  
✅ Database schema unchanged  
✅ Security model unchanged

---

## Success Metrics

### User Experience
- **Login completion rate:** Target 95%+ (currently unknown)
- **Time to login:** Target < 30 seconds
- **Support tickets:** Reduce "forgot tenant URL" by 80%

### Technical
- **Discovery latency:** < 200ms p95
- **Cache hit rate:** > 80%
- **Rate limit effectiveness:** Block malicious attempts

### Business
- **User satisfaction:** Survey scores > 4/5
- **Adoption rate:** 80%+ users using new flow within 3 months
- **Cost savings:** Reduced support burden

---

## Risks & Mitigations

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|------------|
| Email enumeration | Medium | Medium | Rate limiting + CAPTCHA |
| Performance issues | Medium | Low | Caching + query optimization |
| User confusion | Low | Low | Clear UI/UX + help text |
| Database query slow | Medium | Low | Proper indexes already exist |
| Backward compatibility | High | Very Low | Parallel implementation, no breaking changes |

**Overall Risk Level:** 🟢 **LOW** (well-understood problem, proven solution patterns)

---

## Decision Points for Stakeholders

### ✅ Approve for Implementation
- Proceed with Option 1 (Tenant Discovery with Current Schema)
- 4-week implementation timeline
- Phased rollout starting Week 5

### 📝 Request Modifications
- Change email verification requirement?
- Add/remove features from MVP?
- Adjust security controls?

### ❌ Defer/Reject
- What concerns need addressing?
- What alternative approach preferred?

---

## Next Steps if Approved

1. **Day 1:** Create GitHub issues for Sprint 1 tasks
2. **Day 2:** Assign developers and allocate time
3. **Day 3:** Begin implementation of `ITenantDiscoveryService`
4. **Week 1 End:** Backend service complete and tested
5. **Week 2-3:** UI implementation
6. **Week 4:** Testing, security audit, polish
7. **Week 5:** Beta rollout (10% traffic)
8. **Week 6:** Full production rollout

---

## Related Documentation

- Multi-tenancy implementation: `docs/multitenancy-backlog.md`
- Tenant creation flow: `docs/tenant-creation-ui-flow.md`
- Admin guide: `docs/admin-guide.md`
- Developer guide: `docs/developer-guide.md`

---

## Questions?

### Technical Questions
- Review: `tenant-selection-login-flow.md` (full technical spec)
- Contact: Lead Developer

### UX/Design Questions
- Review: `tenant-selection-diagrams.md` (visual mockups)
- Contact: UX Designer

### Security Questions
- Review: Security sections in all docs
- Contact: Security Team Lead

### Business Questions
- Review: Success metrics and rollout plan
- Contact: Product Manager

---

## Recommendation

**✅ APPROVE THIS PROPOSAL**

**Reasoning:**
1. Solves critical UX problem (users can't log in without tenant URL)
2. Low implementation risk (no schema changes, backward compatible)
3. Reasonable timeline (4 weeks to MVP)
4. Industry-standard approach (email-first discovery)
5. Strong security controls in place
6. Clear success metrics and rollout plan

**Confidence Level:** HIGH  
**Recommended Action:** Proceed with implementation  
**Estimated ROI:** High (reduced support costs + improved user satisfaction)

---

## Sign-Off

- [ ] **Product Owner:** Approved / Needs revision / Rejected
- [ ] **Tech Lead:** Approved / Needs revision / Rejected
- [ ] **Security Lead:** Approved / Needs revision / Rejected
- [ ] **UX Designer:** Approved / Needs revision / Rejected

**Date:** _______________

**Comments:**

---

## Appendix: Quick Links

- 📋 **Full Spec:** [tenant-selection-login-flow.md](./tenant-selection-login-flow.md)
- 📝 **Quick Ref:** [tenant-selection-quickref.md](./tenant-selection-quickref.md)
- 🎨 **Diagrams:** [tenant-selection-diagrams.md](./tenant-selection-diagrams.md)
- 🗂️ **Backlog:** Ready for conversion to GitHub issues/Jira tickets
