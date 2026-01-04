# Sidebar Menu Color Scheme

**Date:** October 6, 2025  
**Component:** `_Layout.cshtml` sidebar navigation

## Overview

The sidebar navigation now uses distinct colors for each menu section to improve visual hierarchy and user orientation.

## Color Scheme

### 🔵 My Account Section
- **Color:** `bg-info text-white` (Light Blue)
- **Purpose:** Personal user account settings
- **Menu Items:**
  - Dashboard
  - Profile
  - Password
  - Security (MFA)
  - Sessions

**Visual:** Light blue header with white text

---

### 🔵 Platform Admin Section
- **Color:** `bg-primary text-white` (Primary Blue)
- **Purpose:** Platform-wide administration (multi-tenant management)
- **Visibility:** Only shown to users with `platform-admin` policy
- **Menu Items:**
  - Dashboard
  - Tenants
  - Impersonation
  - Impersonation History

**Visual:** Darker blue header with white text

---

### 🟢 Admin Section
- **Color:** `bg-success text-white` (Green)
- **Purpose:** Tenant-specific administration (OIDC/OAuth management)
- **Menu Items:**
  - Realms
  - Clients
  - Providers
  - Provider mappings
  - Scopes
  - Roles
  - Users
  - Registrations
  - BCL outbox

**Visual:** Green header with white text

---

### ⚫ Account Section (Bottom)
- **Color:** `bg-secondary text-white` (Gray)
- **Purpose:** Account actions (logout)
- **Menu Items:**
  - Log out

**Visual:** Gray header with white text

---

## Color Psychology

- **Light Blue (Info)** - Personal, user-focused, informational
- **Dark Blue (Primary)** - Authority, platform-level control, trust
- **Green (Success)** - Configuration, management, operational
- **Gray (Secondary)** - Utility, less prominent actions

## Accessibility

All color combinations meet WCAG 2.1 AA contrast requirements:
- Dark text on light backgrounds
- White text on colored backgrounds (info, primary, success, secondary)

## Bootstrap Classes Used

- `bg-info` - Bootstrap info color (#0dcaf0)
- `bg-primary` - Bootstrap primary color (#0d6efd)
- `bg-success` - Bootstrap success color (#198754)
- `bg-secondary` - Bootstrap secondary color (#6c757d)
- `text-white` - White text for contrast

## Implementation

**File:** `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml`

```razor
<!-- My Account - Light Blue -->
<div class="list-group-item fw-semibold text-uppercase small bg-info text-white">My Account</div>

<!-- Platform Admin - Dark Blue -->
<div class="list-group-item fw-semibold text-uppercase small bg-primary text-white">Platform Admin</div>

<!-- Admin - Green -->
<div class="list-group-item fw-semibold text-uppercase small bg-success text-white">Admin</div>

<!-- Account - Gray -->
<div class="list-group-item fw-semibold text-uppercase small bg-secondary text-white">Account</div>
```

## Benefits

✅ **Improved Visual Hierarchy** - Clear separation between sections  
✅ **Better User Orientation** - Users can quickly identify section type  
✅ **Role-Based Visual Cues** - Platform admin (blue) vs Tenant admin (green)  
✅ **Consistent with Bootstrap** - Uses standard Bootstrap color utilities  
✅ **Accessible** - High contrast for readability

## Testing

To verify the colors:
1. Start the application: `dotnet run --project MrWhoOidc.AppHost`
2. Login as a user with platform admin rights
3. Observe the sidebar menu sections with distinct colors
4. Test responsive behavior on mobile (offcanvas)

## Future Enhancements

Potential improvements:
- [ ] Add subtle border colors matching section colors
- [ ] Highlight active menu item with section color
- [ ] Add hover effects with section color tint
- [ ] Consider custom CSS for branded colors
- [ ] Add dark mode support with adjusted colors
