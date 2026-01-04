# Tenant Selection - Test Plan

## Overview
This document provides step-by-step manual testing scenarios for the email-first tenant discovery feature.

## Prerequisites
- MrWhoOidc server running (local or Docker)
- Multi-tenancy enabled in configuration
- Database seeded with test data (see below)

---

## Test Data Setup

### Create Test Tenants
```sql
-- Tenant 1: "default"
INSERT INTO "Tenants" ("Id", "Slug", "Name", "IssuerUri", "Status", "CreatedAt")
VALUES (gen_random_uuid(), 'default', 'Default Tenant', 'https://localhost:7777', 0, NOW());

-- Tenant 2: "acme"
INSERT INTO "Tenants" ("Id", "Slug", "Name", "IssuerUri", "Status", "CreatedAt")
VALUES (gen_random_uuid(), 'acme', 'ACME Corporation', 'https://localhost:7777/t/acme', 0, NOW());

-- Tenant 3: "globex"
INSERT INTO "Tenants" ("Id", "Slug", "Name", "IssuerUri", "Status", "CreatedAt")
VALUES (gen_random_uuid(), 'globex', 'Globex Corporation', 'https://localhost:7777/t/globex', 0, NOW());
```

### Create Test Users

**Single Tenant User:**
```sql
-- alice@example.com - only in "default" tenant
INSERT INTO "Users" ("Id", "TenantId", "Username", "NormalizedUsername", "Email", "NormalizedEmail", "PasswordHash", "CreatedAt")
VALUES (
    gen_random_uuid(),
    (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'default'),
    'alice',
    'ALICE',
    'alice@example.com',
    'ALICE@EXAMPLE.COM',
    '$2a$11$hashed_password_here', -- Use actual Argon2id/BCrypt hash
    NOW()
);
```

**Multi-Tenant User:**
```sql
-- bob@example.com - in "default" AND "acme" tenants
-- Default tenant
INSERT INTO "Users" ("Id", "TenantId", "Username", "NormalizedUsername", "Email", "NormalizedEmail", "PasswordHash", "CreatedAt")
VALUES (
    gen_random_uuid(),
    (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'default'),
    'bob',
    'BOB',
    'bob@example.com',
    'BOB@EXAMPLE.COM',
    '$2a$11$hashed_password_here',
    NOW()
);

-- ACME tenant
INSERT INTO "Users" ("Id", "TenantId", "Username", "NormalizedUsername", "Email", "NormalizedEmail", "PasswordHash", "CreatedAt")
VALUES (
    gen_random_uuid(),
    (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'acme'),
    'bob.smith',
    'BOB.SMITH',
    'bob@example.com',
    'BOB@EXAMPLE.COM',
    '$2a$11$hashed_password_here',
    NOW()
);
```

**User with Alternative Email:**
```sql
-- charlie@work.com (primary) also uses charlie@personal.com (alternative)
INSERT INTO "Users" ("Id", "TenantId", "Username", "NormalizedUsername", "Email", "NormalizedEmail", "PasswordHash", "CreatedAt")
VALUES (
    gen_random_uuid(),
    (SELECT "Id" FROM "Tenants" WHERE "Slug" = 'globex'),
    'charlie',
    'CHARLIE',
    'charlie@work.com',
    'CHARLIE@WORK.COM',
    '$2a$11$hashed_password_here',
    NOW()
);

-- Add alternative email
INSERT INTO "UserAlternativeEmail" ("Id", "UserId", "Email", "NormalizedEmail", "IsVerified", "CreatedAt")
VALUES (
    gen_random_uuid(),
    (SELECT "Id" FROM "Users" WHERE "Email" = 'charlie@work.com'),
    'charlie@personal.com',
    'CHARLIE@PERSONAL.COM',
    true, -- Must be verified to be included in discovery
    NOW()
);
```

---

## Test Scenarios

### Scenario 1: Single Tenant User (Auto-Redirect)

**User**: alice@example.com  
**Expected Flow**: Email → Auto-redirect to login

**Steps:**
1. Navigate to `http://localhost:7777/DiscoverTenant`
2. Enter email: `alice@example.com`
3. Click "Continue"

**Expected Results:**
- ✅ Redirects to `/Login?email=alice@example.com`
- ✅ Username field pre-filled with "alice@example.com"
- ✅ Message shows: "Signing in as **alice@example.com**"
- ✅ "Not you?" link visible
- ✅ Enter password and sign in successfully

**Verification:**
```
Database Query:
SELECT t."Slug", u."Email" 
FROM "Users" u 
JOIN "Tenants" t ON u."TenantId" = t."Id" 
WHERE u."NormalizedEmail" = 'ALICE@EXAMPLE.COM';

Expected: 1 row (default tenant)
```

---

### Scenario 2: Multi-Tenant User (Selection UI)

**User**: bob@example.com  
**Expected Flow**: Email → Tenant selection → Login

**Steps:**
1. Navigate to `http://localhost:7777/DiscoverTenant`
2. Enter email: `bob@example.com`
3. Click "Continue"
4. See tenant selection page with 2 cards:
   - Default Tenant
   - ACME Corporation
5. Click on "ACME Corporation" card
6. Redirected to `/t/acme/Login?email=bob@example.com`

**Expected Results:**
- ✅ Shows 2 tenant cards (not 1 or 3+)
- ✅ Each card shows tenant name and logo
- ✅ Cards are clickable
- ✅ "Remember my choice" checkbox visible
- ✅ Clicking card redirects to correct tenant login
- ✅ Login page shows "Signing in as **bob@example.com**"
- ✅ Login page URL includes `/t/acme/` prefix

**Verification:**
```
Database Query:
SELECT t."Slug", u."Username", u."Email" 
FROM "Users" u 
JOIN "Tenants" t ON u."TenantId" = t."Id" 
WHERE u."NormalizedEmail" = 'BOB@EXAMPLE.COM';

Expected: 2 rows (default and acme)
```

---

### Scenario 3: Alternative Email Discovery

**User**: charlie@personal.com (alternative email)  
**Expected Flow**: Email → Auto-redirect (1 tenant found via alternative)

**Steps:**
1. Navigate to `http://localhost:7777/DiscoverTenant`
2. Enter email: `charlie@personal.com` (NOT primary email)
3. Click "Continue"

**Expected Results:**
- ✅ Finds user via alternative email
- ✅ Redirects to `/t/globex/Login?email=charlie@personal.com`
- ✅ Username field pre-filled with "charlie@personal.com"
- ✅ Can log in using primary username or primary email

**Verification:**
```
Database Query:
SELECT t."Slug", u."Email", uae."Email" AS "AlternativeEmail"
FROM "UserAlternativeEmail" uae
JOIN "Users" u ON uae."UserId" = u."Id"
JOIN "Tenants" t ON u."TenantId" = t."Id"
WHERE uae."NormalizedEmail" = 'CHARLIE@PERSONAL.COM' AND uae."IsVerified" = true;

Expected: 1 row (globex tenant)
```

---

### Scenario 4: Unknown Email (Not Found)

**User**: unknown@example.com  
**Expected Flow**: Email → Error message

**Steps:**
1. Navigate to `http://localhost:7777/DiscoverTenant`
2. Enter email: `unknown@example.com`
3. Click "Continue"

**Expected Results:**
- ✅ Page reloads with error message
- ✅ Error: "No account found with this email address. Please check and try again."
- ✅ Email field still contains entered value
- ✅ No redirect occurs
- ✅ Status code: 200 (not 404 - prevents email enumeration timing attacks)

---

### Scenario 5: Rate Limiting (Security)

**Expected Flow**: 6th request within 1 minute gets rate limited

**Steps:**
1. Navigate to `http://localhost:7777/DiscoverTenant`
2. Submit email 5 times rapidly (any email)
3. On 6th submission, expect rate limit error

**Expected Results:**
- ✅ First 5 requests succeed (200 OK or validation error)
- ✅ 6th request fails with HTTP 429 (Too Many Requests)
- ✅ Error page or message shown to user
- ✅ After waiting 1 minute, requests succeed again

**Verification:**
```
Browser DevTools → Network Tab:
- First 5 requests: Status 200
- 6th request: Status 429
- Response header: Retry-After: XX seconds
```

---

### Scenario 6: "Not You?" Link

**User**: alice@example.com (single tenant)  
**Expected Flow**: Login → "Not you?" → Back to discovery

**Steps:**
1. Navigate to `/DiscoverTenant`
2. Enter `alice@example.com`
3. Redirected to `/Login?email=alice@example.com`
4. Click "Not you?" link
5. Redirected back to `/DiscoverTenant`

**Expected Results:**
- ✅ "Not you?" link visible on login page
- ✅ Clicking link redirects to `/DiscoverTenant`
- ✅ ReturnUrl preserved (if present)
- ✅ Email field on discovery page is empty (fresh start)

---

### Scenario 7: Session Expiration

**User**: bob@example.com (multi-tenant)  
**Expected Flow**: Discovery → Wait 11 minutes → Session expired error

**Steps:**
1. Navigate to `/DiscoverTenant`
2. Enter `bob@example.com`
3. See tenant selection page
4. **Do NOT select a tenant**
5. Wait 11 minutes (idle timeout = 10 minutes)
6. Try to click a tenant card

**Expected Results:**
- ✅ Shows error: "Your session has expired. Please start over."
- ✅ Tenant list no longer available in session
- ✅ Redirected back to `/DiscoverTenant`
- ✅ Must re-enter email to continue

---

### Scenario 8: ReturnUrl Preservation

**User**: alice@example.com  
**Expected Flow**: OAuth flow → Discovery → Login → Redirect to client

**Steps:**
1. Start OAuth flow from client app
2. Client redirects to: `/authorize?client_id=...&redirect_uri=...&returnUrl=...`
3. Intercepted by discovery middleware (or user navigates to `/DiscoverTenant?returnUrl=...`)
4. Enter email: `alice@example.com`
5. Redirected to `/Login?email=alice@example.com&returnUrl=...`
6. Log in successfully
7. Redirected to original returnUrl

**Expected Results:**
- ✅ ReturnUrl preserved through discovery flow
- ✅ ReturnUrl preserved in Login page hidden field
- ✅ After login, user redirected to original destination
- ✅ OAuth flow completes successfully

---

### Scenario 9: Tenant-Prefixed Discovery (Direct Access)

**Expected Flow**: Access discovery at tenant-specific URL

**Steps:**
1. Navigate to `http://localhost:7777/t/acme/DiscoverTenant`
2. Enter email: `bob@example.com`
3. Verify tenant selection still works

**Expected Results:**
- ✅ Discovery page loads successfully
- ✅ Shows same email input form
- ✅ Tenant detection still works correctly
- ✅ Can access via both root `/DiscoverTenant` and tenant-prefixed `/t/{slug}/DiscoverTenant`

---

### Scenario 10: localStorage Preference (Remember Choice)

**User**: bob@example.com (multi-tenant)  
**Expected Flow**: Select tenant → Check "Remember" → Next time auto-redirects

**Steps:**
1. First visit: Enter `bob@example.com`
2. Tenant selection page appears
3. Check "Remember my choice" checkbox
4. Click on "ACME Corporation"
5. Second visit: Enter `bob@example.com` again

**Expected Results (First Visit):**
- ✅ Checkbox visible on selection page
- ✅ Clicking tenant sets localStorage key
- ✅ Redirects to selected tenant's login page

**Expected Results (Second Visit):**
- ✅ Auto-redirects to ACME tenant (skips selection)
- ✅ No tenant selection page shown
- ✅ Goes directly to `/t/acme/Login?email=bob@example.com`

**Verification:**
```javascript
// Browser Console:
localStorage.getItem('tenant-preference:bob@example.com')
// Expected: { "email": "bob@example.com", "tenantSlug": "acme", "timestamp": 1234567890 }
```

**Clearing Preference:**
```javascript
// Clear preference to test fresh selection:
localStorage.clear();
```

---

## Test Checklist

### Functional Tests
- [ ] Scenario 1: Single tenant auto-redirect
- [ ] Scenario 2: Multi-tenant selection
- [ ] Scenario 3: Alternative email discovery
- [ ] Scenario 4: Unknown email error
- [ ] Scenario 5: Rate limiting enforcement
- [ ] Scenario 6: "Not you?" link navigation
- [ ] Scenario 7: Session expiration handling
- [ ] Scenario 8: ReturnUrl preservation
- [ ] Scenario 9: Tenant-prefixed routes
- [ ] Scenario 10: localStorage preference

### Browser Compatibility
- [ ] Chrome/Edge (latest)
- [ ] Firefox (latest)
- [ ] Safari (latest)
- [ ] Mobile browsers (iOS Safari, Chrome Mobile)

### Accessibility
- [ ] Keyboard navigation (Tab, Enter)
- [ ] Screen reader compatibility
- [ ] Focus indicators visible
- [ ] Error messages read by assistive tech

### Performance
- [ ] Discovery query < 100ms (with cache miss)
- [ ] Page load time < 1s
- [ ] Session storage overhead < 1KB per user
- [ ] Rate limiting memory usage reasonable

---

## Troubleshooting Guide

### Issue: Discovery Always Returns "No account found"

**Checks:**
1. Verify email normalization (should be uppercase)
   ```sql
   SELECT "NormalizedEmail" FROM "Users" WHERE "Email" = 'alice@example.com';
   ```
2. Check service is registered in DI
3. Verify tenant is Active (`Status = 0`)
4. Check database connection

### Issue: Session Not Persisting

**Checks:**
1. Browser allows cookies
2. Session middleware added to pipeline
3. Session cookie visible in DevTools
4. Timeout not expired (10 minutes)

### Issue: Rate Limiting Not Working

**Checks:**
1. Rate limiting middleware enabled
2. Policy "email-discovery" registered
3. Attribute applied to page model
4. Try from different IP (if behind proxy)

---

## Automated Test Stubs (TODO)

### Unit Tests
```csharp
[TestClass]
public class TenantDiscoveryServiceTests
{
    [TestMethod]
    public async Task FindTenantsByEmailAsync_SingleTenant_ReturnsOneTenant() { }
    
    [TestMethod]
    public async Task FindTenantsByEmailAsync_MultiTenant_ReturnsMultipleTenants() { }
    
    [TestMethod]
    public async Task FindTenantsByEmailAsync_AlternativeEmail_FindsTenant() { }
    
    [TestMethod]
    public async Task FindTenantsByEmailAsync_UnverifiedAlternative_IgnoresEmail() { }
    
    [TestMethod]
    public async Task FindTenantsByEmailAsync_UnknownEmail_ReturnsEmptyList() { }
    
    [TestMethod]
    public async Task GetPreferredTenantAsync_CookiePresent_ReturnsPreferred() { }
}
```

### Integration Tests
```csharp
[TestClass]
public class TenantDiscoveryIntegrationTests
{
    [TestMethod]
    public async Task DiscoverTenant_SingleTenant_RedirectsToLogin() { }
    
    [TestMethod]
    public async Task DiscoverTenant_MultiTenant_ShowsSelectionPage() { }
    
    [TestMethod]
    public async Task DiscoverTenant_RateLimitExceeded_Returns429() { }
    
    [TestMethod]
    public async Task SelectTenant_SessionExpired_ShowsError() { }
}
```

---

**Last Updated**: 2025  
**Status**: Ready for manual testing  
**Next Steps**: Execute scenarios 1-10, create automated tests
