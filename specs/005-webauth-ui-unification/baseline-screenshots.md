# Baseline Screenshots - UI Unification

**Purpose**: Baseline visual reference for comparing before/after UI changes  
**Date Created**: November 11, 2025  
**Status**: 📸 PENDING MANUAL CAPTURE

## Required Screenshots

Capture screenshots of the following 10 representative pages **BEFORE** applying design system refactoring:

### Auth Flow Pages

1. **Login** (`/Login`)
   - Path: `screenshots/baseline/01-login.png`
   - Viewports: Mobile (375px), Tablet (768px), Desktop (1200px)
   
2. **Consent** (`/Consent`)
   - Path: `screenshots/baseline/02-consent.png`
   - Viewports: Mobile, Tablet, Desktop
   
3. **SelectTenant** (`/SelectTenant`)
   - Path: `screenshots/baseline/03-select-tenant.png`
   - Viewports: Mobile, Tablet, Desktop

### Admin Pages

4. **Admin Users Index** (`/Admin/Users/Index`)
   - Path: `screenshots/baseline/04-admin-users.png`
   - Viewports: Mobile, Tablet, Desktop
   
5. **Admin Clients Index** (`/Admin/Clients/Index`)
   - Path: `screenshots/baseline/05-admin-clients.png`
   - Viewports: Mobile, Tablet, Desktop
   
6. **Admin Roles Index** (`/Admin/Roles/Index`)
   - Path: `screenshots/baseline/06-admin-roles.png`
   - Viewports: Mobile, Tablet, Desktop

### Account Pages

7. **Account Profile** (`/Account/Profile`)
   - Path: `screenshots/baseline/07-account-profile.png`
   - Viewports: Mobile, Tablet, Desktop

### Error Pages

8. **Error** (`/Error`)
   - Path: `screenshots/baseline/08-error.png`
   - Viewports: Mobile, Tablet, Desktop
   
9. **NotFound** (`/NotFound` or trigger 404)
   - Path: `screenshots/baseline/09-not-found.png`
   - Viewports: Mobile, Tablet, Desktop

### Public Pages

10. **Index/Home** (`/Index`)
    - Path: `screenshots/baseline/10-index.png`
    - Viewports: Mobile, Tablet, Desktop

## Instructions

### Browser Setup

- **Browser**: Latest Chrome or Edge
- **Clear Cache**: Yes (ensure fresh load of CSS)
- **Extensions**: Disable ad blockers and browser extensions
- **Authentication**: Login as admin user to access all pages

### Capture Tool Options

**Option 1: Browser DevTools**
1. Open DevTools (F12)
2. Toggle Device Toolbar (Ctrl+Shift+M)
3. Set viewport width (375px, 768px, 1200px)
4. Capture full-page screenshot: Ctrl+Shift+P → "Capture full size screenshot"

**Option 2: Browser Extension**
- Use "GoFullPage" or "Awesome Screenshot" for full-page captures

**Option 3: Manual Tools**
- Use Snipping Tool (Windows) or Screenshot tool (Mac)
- Ensure entire page is visible

### Naming Convention

```
screenshots/baseline/{number}-{page-name}-{viewport}.png
```

Examples:
- `screenshots/baseline/01-login-mobile.png`
- `screenshots/baseline/01-login-tablet.png`
- `screenshots/baseline/01-login-desktop.png`

### Verification Checklist

After capturing screenshots:

- [ ] All 10 pages captured
- [ ] 3 viewports per page (30 total screenshots)
- [ ] Screenshots are full-page (not just above-the-fold)
- [ ] Filenames follow naming convention
- [ ] Screenshots stored in `MrWhoOidc.WebAuth/screenshots/baseline/`
- [ ] Create comparison directory: `MrWhoOidc.WebAuth/screenshots/after/`

## Post-Refactoring Comparison

After completing UI refactoring tasks:

1. Capture new screenshots in `screenshots/after/` directory
2. Compare side-by-side:
   - Fonts should be identical
   - Colors should be identical
   - Spacing should be identical
   - Component styling should be identical
   - Layout should be identical
3. Document any visual differences in `screenshots/comparison-notes.md`

## Success Criteria

✅ Before and after screenshots show **identical visual appearance**
✅ All inline styles replaced with design system classes
✅ Responsive behavior consistent across viewports
✅ No visual regressions introduced

---

**Note**: This is a manual task. Screenshots cannot be automated in this environment. Complete this before proceeding with page refactoring (Phase 3 tasks).
