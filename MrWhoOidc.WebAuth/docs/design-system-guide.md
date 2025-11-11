# WebAuth Design System Guide

**Version**: 1.0.0
**Last Updated**: November 11, 2025
**Purpose**: Unified design system documentation for MrWhoOidc.WebAuth project

## Overview

The WebAuth design system provides consistent UI patterns, design tokens, and reusable component classes to ensure visual consistency across all pages in the MrWhoOidc.WebAuth application.

**Location**: `wwwroot/css/design-system.css`

## Table of Contents

1. [Design Tokens](#design-tokens)
2. [Core Components](#core-components)
3. [Icon Utilities](#icon-utilities)
4. [Layout Utilities](#layout-utilities)
5. [Accessibility Features](#accessibility-features)
6. [Responsive Patterns](#responsive-patterns)
7. [Migration Guide](#migration-guide)
8. [Examples](#examples)

## 

## Design Tokens

Design tokens are CSS custom properties that centralize all design values. Always use tokens instead of hardcoded values.

### Colors

```css
:root {
  --color-primary: #0d6efd;
  --color-secondary: #6c757d;
  --color-success: #198754;
  --color-danger: #dc3545;
  --color-warning: #ffc107;
  --color-info: #0dcaf0;
  --color-light: #f8f9fa;
  --color-dark: #212529;
}
```

**Usage**:

```css
.custom-element {
  color: var(--color-primary);
  background-color: var(--color-light);
}
```

### Spacing Scale

```css
:root {
  --space-xs: 0.25rem;   /*4px*/
  --space-sm: 0.5rem;    /*8px*/
  --space-md: 1rem;      /*16px*/
  --space-lg: 1.5rem;    /*24px*/
  --space-xl: 2rem;      /*32px*/
  --space-2xl: 3rem;     /*48px*/
  --space-3xl: 4rem;     /*64px*/
}
```

### Typography

```css
:root {
  --font-family-base: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif;
  --font-size-sm: 0.875rem;
  --font-size-base: 1rem;
  --font-size-lg: 1.125rem;
  --font-size-xl: 1.25rem;
  --font-weight-normal: 400;
  --font-weight-semibold: 600;
  --font-weight-bold: 700;
}
```

### Border Radius

```css
:root {
  --radius-sm: 0.375rem;
  --radius-md: 0.5rem;
  --radius-lg: 0.75rem;
  --radius-xl: 1rem;
}
```

### Shadows

```css
:root {
  --shadow-sm: 0 1px 2px 0 rgba(0, 0, 0, 0.05);
  --shadow-md: 0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06);
  --shadow-lg: 0 10px 15px -3px rgba(0, 0, 0, 0.1), 0 4px 6px -2px rgba(0, 0, 0, 0.05);
}
```

### Transitions

```css
:root {
  --transition-base: 0.2s ease;
  --transition-fast: 0.15s ease;
}
```

## 

## Core Components

### Page Header

Used for main page titles with optional subtitle and action buttons.

**HTML Structure**:

```html
<div class="page-header">
  <div class="page-header__content">
    <h1 class="page-header__title">
      <i class="bi bi-people icon-md icon-primary"></i>
      Users
    </h1>
    <p class="page-header__subtitle">Manage user accounts and permissions</p>
  </div>
  <div class="page-header__actions">
    <a class="btn btn-success" asp-page="Add">
      <i class="bi bi-plus-lg"></i> Add User
    </a>
  </div>
</div>
```

**Classes**:

- `.page-header`: Container (flex layout, responsive)
- `.page-header__content`: Title and subtitle wrapper
- `.page-header__title`: Main heading
- `.page-header__subtitle`: Descriptive text
- `.page-header__actions`: Button group

**Responsive Behavior**: Stacks vertically on mobile, horizontal layout on tablet+.

---

### Data Table

Responsive table with consistent styling and mobile-friendly display.

**HTML Structure**:
```html
<div class="card">
  <div class="table-responsive">
    <table class="data-table">
      <thead>
        <tr>
          <th><i class="bi bi-person icon-sm"></i> Name</th>
          <th>Email</th>
          <th class="text-end">Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr class="data-table__row">
          <td data-label="Name">John Doe</td>
          <td data-label="Email">john@example.com</td>
          <td data-label="Actions" class="text-end">
            <a class="btn btn-sm btn-outline-secondary" href="/edit/1">
              <i class="bi bi-pencil"></i> Edit
            </a>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</div>
```

**Classes**:
- `.data-table`: Base table class
- `.data-table__row`: Table row with hover state

**Important**: Always include `data-label` attributes on `<td>` elements for mobile responsive display.

**Mobile Behavior**: On screens < 768px, table transforms to card layout with labeled fields.

---

### Auth Card

Centered card component for authentication pages (login, consent, etc.).

**HTML Structure**:
```html
<div class="auth-card">
  <div class="card-body p-4">
    <div class="auth-card__header">
      <img src="@Model.LogoUrl" class="auth-card__logo" alt="Logo" />
      <h2 class="auth-card__title">Sign in to your account</h2>
      <p class="auth-card__subtitle">Enter your credentials to continue</p>
    </div>
    
    <form method="post">
      <!-- Form fields -->
    </form>
  </div>
</div>
```

**Classes**:
- `.auth-card`: Card container (max-width: 420px, centered, shadow)
- `.auth-card__header`: Header section with centered text
- `.auth-card__logo`: Logo image (max-width/height: 100px)
- `.auth-card__title`: Main heading
- `.auth-card__subtitle`: Descriptive text

---

### Form Group

Standard form input pattern with consistent spacing.

**HTML Structure**:
```html
<div class="form-group">
  <label for="username" class="form-label">
    <i class="bi bi-person icon-sm"></i> Username
  </label>
  <input type="text" id="username" class="form-control" placeholder="Enter username" />
  <div class="form-text">Help text or validation message</div>
</div>
```

**Classes**:
- `.form-group`: Container with bottom margin (var(--space-lg))

**Note**: Use Bootstrap's `.form-label`, `.form-control`, `.form-text` for form elements.

---

### Action Button Group

Consistent spacing for button clusters.

**HTML Structure**:
```html
<div class="action-buttons">
  <button type="submit" class="btn btn-primary">Save</button>
  <a href="/cancel" class="btn btn-secondary">Cancel</a>
</div>
```

**Classes**:
- `.action-buttons`: Flex container with gap (var(--space-sm))

---

## Icon Utilities

### Icon Sizes

```html
<i class="bi bi-person icon-xs"></i>   <!-- 16px -->
<i class="bi bi-person icon-sm"></i>   <!-- 20px -->
<i class="bi bi-person icon-md"></i>   <!-- 24px -->
<i class="bi bi-person icon-lg"></i>   <!-- 32px -->
<i class="bi bi-person icon-xl"></i>   <!-- 48px -->
<i class="bi bi-person icon-2xl"></i>  <!-- 64px -->
```

### Icon Colors

```html
<i class="bi bi-check icon-primary"></i>
<i class="bi bi-check icon-success"></i>
<i class="bi bi-check icon-danger"></i>
<i class="bi bi-check icon-warning"></i>
<i class="bi bi-check icon-muted"></i>
```

**Best Practice**: Pair icon colors with context (success icons with success alerts, danger icons with error messages, etc.).

---

## Layout Utilities

### Container Widths

```html
<!-- Auth pages: narrow centered container -->
<div class="auth-container">
  <!-- Max-width: 420px -->
</div>

<!-- Content pages: standard container -->
<div class="content-container">
  <!-- Max-width: 750px -->
</div>
```

**Note**: For full-width layouts, use Bootstrap's `.container` or `.container-fluid`.

---

## Accessibility Features

The design system includes built-in accessibility support:

### Focus States

Enhanced focus-visible states with 2px outline and offset for keyboard navigation.

### Reduced Motion Support

Respects `prefers-reduced-motion` media query to disable animations for users with motion sensitivity.

### High Contrast Mode

Automatically adjusts colors and shadows for users with `prefers-contrast: high` setting.

---

## Responsive Patterns

### Mobile-First Approach

All components are designed mobile-first with progressive enhancement for larger screens.

**Breakpoints** (Bootstrap 5):
- `xs`: < 576px (default, mobile)
- `sm`: ≥ 576px
- `md`: ≥ 768px (tablet)
- `lg`: ≥ 992px (desktop)
- `xl`: ≥ 1200px
- `xxl`: ≥ 1400px

### Responsive Utilities

```html
<!-- Hidden on mobile, visible on tablet+ -->
<span class="d-none d-md-inline">Full label text</span>

<!-- Visible on mobile, hidden on desktop -->
<span class="d-md-none">Short label</span>

<!-- Responsive table cell -->
<td data-label="Email" class="d-none d-md-table-cell">
  john@example.com
</td>
```

---

## Migration Guide

### Step-by-Step Process

1. **Identify inline styles**: Search for `style="..."` in .cshtml files
2. **Find equivalent class**: Consult this guide for component patterns
3. **Replace**: Apply design system class or CSS custom property
4. **Test**: Verify visual appearance on mobile, tablet, and desktop

### Common Replacements

| Inline Style | Design System Class |
|--------------|---------------------|
| `style="max-width: 420px"` | `class="auth-card"` or `class="auth-container"` |
| `style="font-size: 3rem"` | `class="icon-xl"` (for icons) |
| `style="text-align: center"` | `class="text-center"` (Bootstrap) |
| `style="margin-bottom: 1.5rem"` | Use Bootstrap `mb-3` or define custom class with `var(--space-lg)` |
| `style="display: flex; gap: 0.5rem"` | `class="action-buttons"` or `class="d-flex"` + custom gap |

---

## Examples

### Complete Admin Page

```html
@page "/admin/users"
@model UsersIndexModel

<!-- Page Header -->
<div class="page-header">
  <div class="page-header__content">
    <h1 class="page-header__title">
      <i class="bi bi-people icon-md icon-primary"></i>
      Users
    </h1>
    <p class="page-header__subtitle">Manage user accounts and permissions</p>
  </div>
  <div class="page-header__actions">
    <a class="btn btn-success" asp-page="Add">
      <i class="bi bi-plus-lg"></i> Add User
    </a>
  </div>
</div>

<!-- Alert -->
@if (TempData["Success"] != null)
{
  <div class="alert alert-success alert-dismissible fade show" role="alert">
    <i class="bi bi-check-circle icon-sm"></i>
    @TempData["Success"]
    <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
  </div>
}

<!-- Data Table -->
<div class="card">
  <div class="table-responsive">
    <table class="data-table">
      <thead>
        <tr>
          <th><i class="bi bi-person icon-sm"></i> Name</th>
          <th><i class="bi bi-envelope icon-sm"></i> Email</th>
          <th class="text-end">Actions</th>
        </tr>
      </thead>
      <tbody>
        @foreach (var user in Model.Users)
        {
          <tr class="data-table__row">
            <td data-label="Name">@user.Name</td>
            <td data-label="Email">@user.Email</td>
            <td data-label="Actions" class="text-end">
              <a href="/admin/users/edit/@user.Id" 
                 class="btn btn-sm btn-outline-secondary">
                <i class="bi bi-pencil"></i> Edit
              </a>
            </td>
          </tr>
        }
      </tbody>
    </table>
  </div>
</div>
```

### Complete Auth Page

```html
@page "/login"
@model LoginModel
@{
  Layout = "_AuthLayout";
}

<div class="auth-card">
  <div class="card-body p-4">
    <div class="auth-card__header">
      @if (!string.IsNullOrEmpty(Model.LogoUrl))
      {
        <img src="@Model.LogoUrl" class="auth-card__logo" alt="Logo" />
      }
      <h2 class="auth-card__title">Sign in to your account</h2>
      <p class="auth-card__subtitle">Enter your credentials to continue</p>
    </div>

    <form method="post">
      <div class="form-group">
        <label asp-for="Username" class="form-label">
          <i class="bi bi-person icon-sm"></i> Username
        </label>
        <input asp-for="Username" class="form-control" 
               placeholder="Enter username" />
        <span asp-validation-for="Username" class="text-danger"></span>
      </div>

      <div class="form-group">
        <label asp-for="Password" class="form-label">
          <i class="bi bi-lock icon-sm"></i> Password
        </label>
        <input asp-for="Password" type="password" class="form-control" />
        <span asp-validation-for="Password" class="text-danger"></span>
      </div>

      <div class="action-buttons">
        <button type="submit" class="btn btn-primary w-100">
          <i class="bi bi-box-arrow-in-right"></i> Sign In
        </button>
      </div>
    </form>
  </div>
</div>
```

---

## Best Practices

### DO ✅

- Use CSS custom properties for all colors, spacing, and sizes
- Apply component classes for common patterns
- Use icon size and color classes
- Add `data-label` attributes to table cells for mobile responsiveness
- Test layouts on mobile (375px), tablet (768px), and desktop (1200px)
- Include icons in alerts, buttons, and labels for visual consistency

### DON'T ❌

- Use inline styles for static values (colors, sizes, spacing)
- Hardcode colors, spacing, or font sizes
- Create duplicate component styles in individual pages
- Use arbitrary icon sizes (use `.icon-*` classes)
- Mix design system classes with arbitrary inline styles

---

## Support & Maintenance

- **Design System Source**: `MrWhoOidc.WebAuth/wwwroot/css/design-system.css`
- **Examples**: See existing refactored pages in `MrWhoOidc.WebAuth/Pages/`
- **Bootstrap Documentation**: <https://getbootstrap.com/docs/5.3/>
- **Bootstrap Icons**: <https://icons.getbootstrap.com/>

**Questions or Issues?** Consult the design system CSS file comments or refer to example pages.
