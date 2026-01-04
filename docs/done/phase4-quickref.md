# Phase 4: User Self-Service Portal - Quick Reference

## Current Status (Day 1 - Oct 6, 2025)

✅ **Completed:** Dashboard + Profile (2 of 8 pages)  
⏳ **In Progress:** Password + Security (Stage 3-4)  
📅 **Next:** Sessions, Consents, Linked, Emails

## Quick Navigation

| Page | Route | Status | Features |
|------|-------|--------|----------|
| Dashboard | `/Account` | ✅ Done | Overview with 6 stats cards |
| Profile | `/Account/Profile` | ✅ Done | Edit name, email with validation |
| Password | `/Account/Password` | ⏳ Next | Copy from `/Password/Index` |
| Security | `/Account/Security` | ⏳ Next | Copy from `/Mfa/Index` |
| Sessions | `/Account/Sessions` | 📅 Todo | List & revoke active tokens |
| Consents | `/Account/Consents` | 📅 Todo | Manage authorized apps |
| Linked | `/Account/LinkedAccounts` | 📅 Todo | External identity links |
| Emails | `/Account/Emails` | 📅 Todo | Alternative emails |

## File Locations

```
MrWhoOidc.WebAuth/Pages/Account/
├── _AccountTabs.cshtml          # Shared tabs (all pages)
├── Index.cshtml                 # ✅ Dashboard
├── Index.cshtml.cs              # ✅ Dashboard model
├── Profile.cshtml               # ✅ Profile management
├── Profile.cshtml.cs            # ✅ Profile model
├── Password.cshtml              # ⏳ To be created
├── Password.cshtml.cs           # ⏳ To be created
├── Security.cshtml              # ⏳ To be created
├── Security.cshtml.cs           # ⏳ To be created
├── Sessions.cshtml              # 📅 To be created
├── Sessions.cshtml.cs           # 📅 To be created
├── Consents.cshtml              # 📅 To be created
├── Consents.cshtml.cs           # 📅 To be created
├── LinkedAccounts.cshtml        # 📅 To be created
├── LinkedAccounts.cshtml.cs     # 📅 To be created
├── Emails.cshtml                # 📅 To be created
└── Emails.cshtml.cs             # 📅 To be created
```

## Standard Patterns

### 1. Page Model Authorization
```csharp
[Authorize] // No admin role - all authenticated users
public class YourPageModel(AuthDbContext db) : PageModel
{
    // Implementation
}
```

### 2. Get Current User
```csharp
private async Task<User?> GetCurrentUserAsync(bool tracked = false)
{
    var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (!Guid.TryParse(sub, out var userId)) return null;
    
    var query = db.Users.Where(u => u.Id == userId);
    if (!tracked) query = query.AsNoTracking();
    
    return await query.FirstOrDefaultAsync();
}
```

### 3. Razor Page Template
```cshtml
@page
@model MrWhoOidc.WebAuth.Pages.Account.YourPageModel
@{
    ViewData["Title"] = "Your Page Title";
    ViewData["ActiveAccountTab"] = "pagename"; // dashboard, profile, password, etc.
}

<h1 class="mb-3">@ViewData["Title"]</h1>
<partial name="~/Pages/Account/_AccountTabs.cshtml" />

<!-- Your content here -->
```

### 4. Tenant-Aware Links (in tabs)
```cshtml
@inject ITenantAccessor TenantAccessor
@inject IOptions<MultiTenancyOptions> MultiTenancyOptions
@{
    var tenantPrefix = currentTenant != null && MultiTenancyOptions.Value.Enabled 
        ? $"/t/{currentTenant.Slug}" 
        : "";
}
<a href="@(tenantPrefix)/Account/Profile">Profile</a>
```

## Tomorrow's Checklist

### Morning (2-3 hours)
- [ ] Copy `/Password/Index` → `/Account/Password`
- [ ] Update namespace to `MrWhoOidc.WebAuth.Pages.Account`
- [ ] Add `<partial name="~/Pages/Account/_AccountTabs.cshtml" />`
- [ ] Set `ViewData["ActiveAccountTab"] = "password"`
- [ ] Remove `Layout = "_AuthLayout"`, use default
- [ ] Test password change works

- [ ] Copy `/Mfa/Index` → `/Account/Security`
- [ ] Update namespace to `MrWhoOidc.WebAuth.Pages.Account`
- [ ] Rename class to `SecurityModel`
- [ ] Add account tabs partial
- [ ] Set `ViewData["ActiveAccountTab"] = "security"`
- [ ] Update title to "Security Settings"
- [ ] Test MFA enable/disable works

### Afternoon (3-4 hours)
- [ ] Create `/Account/Sessions.cshtml.cs`
  - Query active tokens: `db.Tokens.Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)`
  - Add revoke handler: `OnPostRevokeAsync(Guid tokenId)`
  - Add revoke all handler: `OnPostRevokeAllAsync()`

- [ ] Create `/Account/Sessions.cshtml`
  - List sessions with icons
  - Show current session badge
  - Add revoke buttons
  - Add "Revoke All Other Sessions" button

- [ ] Build and test
  ```bash
  dotnet build
  docker compose up -d --build
  ```

- [ ] Update `_Layout.cshtml` sidebar
  - Add "My Account" section before "Admin"
  - Add all 8 account page links with icons
  - Use tenant-aware pattern

## Testing After Build

```bash
# 1. Build
dotnet build

# 2. Rebuild docker
docker compose up -d --build

# 3. Open browser
https://localhost:8443

# 4. Login via DiscoverTenant
Tenant: pop-app
User: admin@pop-app
Password: (your password)

# 5. Navigate to account portal
https://localhost:8443/t/pop-app/Account

# 6. Test each page:
- Dashboard: Check all stats display
- Profile: Edit name/email, save
- Password: Change password
- Security: Enable/disable MFA
- Sessions: View active sessions (if Stage 5 done)
```

## Common Issues & Solutions

| Issue | Solution |
|-------|----------|
| Page not found (404) | Check @page directive, namespace, and file name match |
| Tabs don't highlight | Set `ViewData["ActiveAccountTab"]` correctly |
| User data doesn't load | Check `GetCurrentUserAsync()` implementation |
| Links missing tenant prefix | Ensure `ITenantAccessor` injected and `tenantPrefix` used |
| Build errors | Check namespace matches folder structure |

## Documentation

- Full Plan: `docs/phase4-user-self-service-portal-implementation.md`
- Daily Progress: `docs/phase4-progress-day1.md`
- Summary: `docs/phase4-day1-summary.md`
- This Quick Ref: `docs/phase4-quickref.md`

## Commands

```bash
# Build
dotnet build

# Test
dotnet test

# Run locally (Aspire)
dotnet run --project MrWhoOidc.AppHost

# Run with Docker
docker compose up -d --build

# View logs
docker compose logs -f webauth

# Stop
docker compose down
```

## Phase 4 Timeline

| Week | Days | Pages | Status |
|------|------|-------|--------|
| Week 1 | Oct 6-12 | Dashboard, Profile, Password, Security, Sessions | ⏳ In Progress |
| Week 2 | Oct 13-19 | Consents, Linked, Emails | 📅 Planned |
| Week 3 | Oct 20-26 | Polish, testing, docs | 📅 Planned |

## Success Metrics

| Metric | Target | Current |
|--------|--------|---------|
| Pages Complete | 8 | 2 ✅ |
| Authorization | Simple `[Authorize]` | ✅ |
| Tenant-Aware | All links | ✅ |
| Tests Passing | All | ⏳ |
| Build Status | Success | ✅ |
| UI Consistent | Bootstrap cards | ✅ |

---

**Last Updated:** October 6, 2025  
**Status:** Day 1 Complete - Foundation Solid  
**Next Session:** Tomorrow morning - Copy Password + Security pages
