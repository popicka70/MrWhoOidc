# Phase 5A: Tenant Switcher - Testing Guide

## Overview
This document describes how to test the tenant switcher feature that allows users with multi-tenant access to switch between organizations via a navbar dropdown.

## Prerequisites

### 1. Multi-Tenant Mode Enabled
Ensure `appsettings.json` has multi-tenancy enabled:

```json
{
  "MultiTenancy": {
    "Enabled": true,
    "Mode": "PathBased"
  }
}
```

### 2. Database with Multiple Tenants
You need at least 2 tenants in the database. You can:
- Use the PlatformAdmin UI to create tenants
- Use the on-demand tenant seeding endpoint
- Seed via SQL/migrations

### 3. Test User with Multi-Tenant Access
Create a test user with role assignments in multiple tenants:

**Option A: Via Admin UI**
1. Navigate to `/PlatformAdmin/Users`
2. Create or edit a user
3. Assign roles in multiple tenants via "Role Assignments" section

**Option B: Via Database/Seed**
```sql
-- Assuming tenantA and tenantB exist with realmA and realmB
-- User 'testuser' exists
-- Roles 'user' exist in both realms

INSERT INTO UserRoleAssignments (UserId, RoleId, TenantId)
VALUES 
  ((SELECT Id FROM Users WHERE Username = 'testuser'), (SELECT Id FROM Roles WHERE Name = 'user' AND RealmId = (SELECT Id FROM Realms WHERE TenantId = (SELECT Id FROM Tenants WHERE Slug = 'acme'))), (SELECT Id FROM Tenants WHERE Slug = 'acme')),
  ((SELECT Id FROM Users WHERE Username = 'testuser'), (SELECT Id FROM Roles WHERE Name = 'user' AND RealmId = (SELECT Id FROM Realms WHERE TenantId = (SELECT Id FROM Tenants WHERE Slug = 'contoso'))), (SELECT Id FROM Tenants WHERE Slug = 'contoso'));
```

## Test Cases

### Test 1: Tenant Switcher Visibility

**Objective:** Verify the tenant switcher dropdown only appears when user has access to 2+ tenants.

**Steps:**
1. Log in as a user with access to only 1 tenant
2. Observe navbar - tenant switcher should NOT be visible
3. Log out
4. Log in as a user with access to 2+ tenants
5. Observe navbar - tenant switcher SHOULD be visible next to the brand

**Expected Result:**
- Single-tenant users: No dropdown visible
- Multi-tenant users: Dropdown button visible with building icon and current tenant name

---

### Test 2: Current Tenant Display

**Objective:** Verify the dropdown shows the correct current tenant.

**Steps:**
1. Log in as multi-tenant user on tenant A (e.g., `/t/acme/`)
2. Check navbar dropdown button text
3. Navigate to tenant B (e.g., `/t/contoso/`)
4. Check navbar dropdown button text again

**Expected Result:**
- Dropdown button should show "Acme" when on `/t/acme/`
- Dropdown button should show "Contoso" when on `/t/contoso/`
- If no tenant context (rare), shows "Select Tenant"

---

### Test 3: Tenant List Display

**Objective:** Verify the dropdown shows all accessible tenants with correct metadata.

**Steps:**
1. Log in as multi-tenant user
2. Click the tenant switcher dropdown button
3. Observe the list of tenants

**Expected Result:**
- Header: "Switch Tenant"
- Divider line
- List of all tenants user has access to
- Each tenant shows:
  - Icon (shield-check for admin roles, building for regular roles)
  - Tenant name
  - Checkmark icon if it's the current tenant
- Current tenant item should be marked "active" (blue highlight) and disabled

---

### Test 4: Switch to Different Tenant

**Objective:** Verify switching to a different tenant works correctly.

**Steps:**
1. Log in as multi-tenant user on tenant A (`/t/acme/`)
2. Open tenant switcher dropdown
3. Click on tenant B (e.g., "Contoso")
4. Wait for page reload

**Expected Result:**
- Page redirects to `/t/contoso/`
- URL changes to tenant B slug
- Navbar dropdown now shows "Contoso"
- Session preference stored (verify by opening developer tools → Application → Session Storage → `PreferredTenantId`)
- User can now access tenant B resources

---

### Test 5: Admin Role Indicator

**Objective:** Verify admin users see the shield icon in the dropdown.

**Steps:**
1. Create user with:
   - Regular role in tenant A
   - Admin role (platform-admin or tenant-admin) in tenant B
2. Log in and open tenant switcher dropdown
3. Observe icons next to each tenant

**Expected Result:**
- Tenant A: Building icon (bi-building)
- Tenant B: Shield icon (bi-shield-check)

---

### Test 6: Unauthorized Tenant Access Prevention

**Objective:** Verify users cannot switch to tenants they don't have access to.

**Steps:**
1. Log in as user with access to tenant A only
2. Manually POST to `/SwitchTenant` with a different tenant's ID:
```bash
curl -X POST https://localhost:7040/SwitchTenant \
  -d "tenantId=<tenant-B-guid>" \
  -d "returnUrl=/t/contoso/" \
  -H "Cookie: <session-cookie>"
```

**Expected Result:**
- Returns HTTP 403 Forbidden
- User is NOT switched to tenant B
- Session remains unchanged

---

### Test 7: Return URL Preservation

**Objective:** Verify users return to the correct page after switching tenants.

**Steps:**
1. Log in and navigate to `/t/acme/Account/Profile`
2. Open tenant switcher dropdown
3. Click tenant B (Contoso)

**Expected Result:**
- After switching, URL should attempt to navigate to equivalent path: `/t/contoso/Account/Profile`
- If returnUrl is valid, page loads successfully
- If not valid for new tenant, redirects to `/t/contoso/` home page

---

### Test 8: Mobile Responsiveness

**Objective:** Verify tenant switcher works on mobile devices.

**Steps:**
1. Log in on mobile device or browser DevTools mobile view
2. Observe navbar layout
3. Test tenant switcher dropdown

**Expected Result:**
- Tenant switcher visible on mobile (between brand and hamburger menu)
- Dropdown button touch-friendly (min 44px height)
- Dropdown menu properly positioned
- Switching works as expected

---

### Test 9: Session Persistence

**Objective:** Verify tenant preference persists across page navigations.

**Steps:**
1. Log in and switch to tenant B
2. Navigate to various pages (`/t/contoso/Account/Dashboard`, `/t/contoso/Admin/Users`, etc.)
3. Close browser (but keep session alive via cookie)
4. Reopen browser and navigate to `/`

**Expected Result:**
- After step 2: All pages load in tenant B context
- After step 4: User is redirected/resolved to tenant B (preferred tenant from session)
- Session storage key `PreferredTenantId` contains tenant B's GUID

---

### Test 10: No Tenant Context Handling

**Objective:** Verify behavior when user is not in a tenant context.

**Steps:**
1. Log in as multi-tenant user
2. Navigate to a non-tenant-prefixed URL (e.g., `/Account/Dashboard` without `/t/{slug}/`)
3. Observe tenant switcher dropdown

**Expected Result:**
- Dropdown button shows "Select Tenant"
- Clicking a tenant redirects to `/t/{slug}/`
- User can select tenant to enter tenant context

---

## Known Limitations (Phase 5A)

1. **No Tenant Preference on Login:** User must manually select tenant after login. Future enhancement: remember last tenant from previous session.
2. **No "All Tenants" View:** Platform admins cannot view a combined view of all tenants. Each tenant must be accessed individually.
3. **Session-Only Preference:** Tenant preference is session-based, not stored in database. Clearing cookies loses preference.

## Troubleshooting

### Dropdown Not Appearing
- Check: Is user logged in?
- Check: Does user have access to 2+ tenants? Query: 
  ```sql
  SELECT t.Name, COUNT(*) 
  FROM UserRoleAssignments ura
  JOIN Roles r ON ura.RoleId = r.Id
  JOIN Realms rl ON r.RealmId = rl.Id
  JOIN Tenants t ON rl.TenantId = t.Id
  WHERE ura.UserId = '<user-guid>'
  GROUP BY t.Id, t.Name;
  ```
- Check: Is multi-tenancy enabled in appsettings?

### Dropdown Empty
- Check: Does user have ACTIVE role assignments?
- Check: Are tenants ACTIVE (not deleted/suspended)?
- Check database query in `TenantSwitchingService.GetUserTenantsAsync()` returns results

### Switch Fails (403 Forbidden)
- Check: Does user have role assignment in target tenant?
- Check: Is target tenant active?
- Check: Is tenantId GUID valid?

### Switch Redirects to Wrong URL
- Check: Is tenant slug correct in database?
- Check: Does TenantResolutionMiddleware resolve tenant correctly?
- Check: Is returnUrl a valid local URL (no open redirect vulnerability)?

## Next Steps

After verifying tenant switcher works:
1. Implement **Platform Admin Impersonation** (view as tenant admin)
2. Implement **Mobile Responsiveness** improvements (responsive tables, touch-friendly UI)
3. Document complete Phase 5A in `phase5a-complete.md`

