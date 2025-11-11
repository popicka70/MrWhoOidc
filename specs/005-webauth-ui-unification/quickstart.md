# Quickstart: WebAuth Design System

**Purpose**: Developer guide for using the unified design system in MrWhoOidc.WebAuth  
**Audience**: Developers working on WebAuth UI pages  
**Last Updated**: November 11, 2025

## Overview

The WebAuth design system provides a consistent set of CSS classes and design tokens for building and maintaining UI pages. This guide shows you how to use the system effectively.

## Design Tokens (CSS Custom Properties)

All design values are defined as CSS custom properties in `design-system.css`. Use these instead of hardcoded values.

### Colors

```css
/* Usage in custom CSS */
.my-element {
  color: var(--color-primary);
  background-color: var(--color-light);
  border-color: var(--color-secondary);
}
```

Available color tokens:

- `--color-primary`: Primary brand color (#0d6efd)
- `--color-secondary`: Secondary/muted color (#6c757d)
- `--color-success`: Success state (#198754)
- `--color-danger`: Error/danger state (#dc3545)
- `--color-warning`: Warning state (#ffc107)
- `--color-info`: Information state (#0dcaf0)
- `--color-light`: Light background (#f8f9fa)
- `--color-dark`: Dark text/backgrounds (#212529)

### Spacing

```css
/* Usage */
.my-element {
  margin-bottom: var(--space-lg);
  padding: var(--space-md);
  gap: var(--space-sm);
}
```

Spacing scale:

- `--space-xs`: 0.25rem (4px)
- `--space-sm`: 0.5rem (8px)
- `--space-md`: 1rem (16px)
- `--space-lg`: 1.5rem (24px)
- `--space-xl`: 2rem (32px)
- `--space-2xl`: 3rem (48px)
- `--space-3xl`: 4rem (64px)

### Typography

```css
/* Usage */
.my-heading {
  font-family: var(--font-family-base);
  font-weight: var(--font-weight-bold);
  font-size: var(--font-size-lg);
}
```

Typography tokens:

- `--font-family-base`: Main font stack
- `--font-size-sm`: 0.875rem
- `--font-size-base`: 1rem
- `--font-size-lg`: 1.125rem
- `--font-size-xl`: 1.25rem
- `--font-weight-normal`: 400
- `--font-weight-semibold`: 600
- `--font-weight-bold`: 700

### Border Radius

- `--radius-sm`: 0.375rem
- `--radius-md`: 0.5rem
- `--radius-lg`: 0.75rem
- `--radius-xl`: 1rem

### Shadows

- `--shadow-sm`: Subtle shadow for cards
- `--shadow-md`: Medium depth
- `--shadow-lg`: Prominent elevation

### Transitions

- `--transition-base`: 0.2s ease (default)
- `--transition-fast`: 0.15s ease (quick interactions)

## Component Classes

### Page Header

Used for main page titles with optional subtitle and action buttons.

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

- `.page-header`: Container (responsive flex)
- `.page-header__content`: Title and subtitle wrapper
- `.page-header__title`: Main heading
- `.page-header__subtitle`: Descriptive text
- `.page-header__actions`: Button group

### Data Table

Responsive table with consistent styling.

```html
<div class="card">
  <div class="table-responsive">
    <table class="data-table">
      <thead>
        <tr>
          <th>
            <i class="bi bi-person icon-sm"></i> Name
          </th>
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
- `.data-table__row`: Table row (has hover state)
- Use `data-label` attribute for mobile responsive labels

### Auth Card

Centered card for authentication pages (login, consent, etc.).

```html
<div class="auth-card">
  <div class="card-body p-4">
    <div class="auth-card__header">
      <img src="@Model.LogoUrl" class="auth-card__logo" alt="Logo" />
      <h2 class="auth-card__title">Sign in to your account</h2>
      <p class="auth-card__subtitle">Enter your credentials to continue</p>
    </div>
    
    <form method="post">
      <!-- Form content -->
    </form>
  </div>
</div>
```

**Classes**:

- `.auth-card`: Card container (max-width, centered)
- `.auth-card__header`: Header section (centered text)
- `.auth-card__logo`: Logo image (constrained size)
- `.auth-card__title`: Main heading
- `.auth-card__subtitle`: Descriptive text

### Form Group

Standard form input pattern.

```html
<div class="form-group">
  <label for="username" class="form-label">
    <i class="bi bi-person icon-sm"></i> Username
  </label>
  <input type="text" 
         id="username" 
         class="form-control" 
         placeholder="Enter username" />
  <div class="form-text">Validation message or help text</div>
</div>
```

**Classes**:

- `.form-group`: Container with consistent spacing
- Use Bootstrap's `.form-label`, `.form-control`, `.form-text`

### Alert Messages

Consistent styling for success/error/warning/info messages.

```html
<div class="alert alert-success alert-dismissible fade show" role="alert">
  <i class="bi bi-check-circle icon-sm"></i>
  Operation completed successfully!
  <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
</div>
```

Use Bootstrap's alert classes:

- `.alert-success`
- `.alert-danger`
- `.alert-warning`
- `.alert-info`

Add icons for visual consistency:

- Success: `bi-check-circle`
- Danger: `bi-exclamation-triangle`
- Warning: `bi-exclamation-circle`
- Info: `bi-info-circle`

### Action Button Groups

Consistent spacing for button clusters.

```html
<div class="action-buttons">
  <button type="submit" class="btn btn-primary">Save</button>
  <a href="/cancel" class="btn btn-secondary">Cancel</a>
</div>
```

**Classes**:

- `.action-buttons`: Flex container with gap

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

## Layout Utilities

### Container Widths

```html
<!-- Auth pages: narrow centered container -->
<div class="auth-container">
  <!-- Content (max-width: 420px) -->
</div>

<!-- Content pages: standard container -->
<div class="content-container">
  <!-- Content (max-width: 750px) -->
</div>

<!-- Full width: use Bootstrap's .container or .container-fluid -->
```

### Spacing Utilities

Use design token spacing for custom elements:

```html
<div class="mb-lg">  <!-- margin-bottom: var(--space-lg) -->
<div class="p-md">   <!-- padding: var(--space-md) -->
<div class="gap-sm"> <!-- gap: var(--space-sm) for flex/grid -->
```

## Responsive Patterns

### Mobile-First Approach

Components are styled for mobile first, with progressive enhancement for larger screens.

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

Use Bootstrap's responsive utilities:

- `d-none`, `d-{breakpoint}-block`
- `d-{breakpoint}-none`
- `d-{breakpoint}-inline`
- `d-{breakpoint}-table-cell`

## Best Practices

### DO

- ✅ Use CSS custom properties for all colors, spacing, sizes
- ✅ Apply component classes for common patterns
- ✅ Use icon size and color classes
- ✅ Add `data-label` attributes to table cells for mobile responsiveness
- ✅ Test layouts on mobile, tablet, and desktop
- ✅ Include icons in alerts, buttons, and labels for visual consistency

### DON'T

- ❌ Use inline styles for static values (colors, sizes, spacing)
- ❌ Hardcode colors, spacing, or font sizes
- ❌ Create duplicate component styles in individual pages
- ❌ Use arbitrary icon sizes (use .icon-* classes)
- ❌ Mix design system classes with arbitrary inline styles

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

<!-- Filter Card -->
<div class="card mb-xl">
  <div class="card-body">
    <form method="get">
      <div class="row g-3">
        <div class="col-md-9">
          <div class="form-group">
            <label class="form-label">
              <i class="bi bi-search icon-sm"></i> Search
            </label>
            <input type="text" class="form-control" name="search" 
                   placeholder="Search by name or email..." />
          </div>
        </div>
        <div class="col-md-3">
          <button class="btn btn-primary w-100 mt-4" type="submit">
            <i class="bi bi-funnel"></i> Filter
          </button>
        </div>
      </div>
    </form>
  </div>
</div>

<!-- Data Table -->
<div class="card">
  <div class="table-responsive">
    <table class="data-table">
      <thead>
        <tr>
          <th><i class="bi bi-person icon-sm"></i> Name</th>
          <th><i class="bi bi-envelope icon-sm"></i> Email</th>
          <th class="d-none d-md-table-cell">Created</th>
          <th class="text-end">Actions</th>
        </tr>
      </thead>
      <tbody>
        @foreach (var user in Model.Users)
        {
          <tr class="data-table__row">
            <td data-label="Name">@user.Name</td>
            <td data-label="Email">@user.Email</td>
            <td data-label="Created" class="d-none d-md-table-cell">
              @user.CreatedAt.ToLocalTime().ToString("g")
            </td>
            <td data-label="Actions" class="text-end">
              <div class="action-buttons">
                <a href="/admin/users/edit/@user.Id" 
                   class="btn btn-sm btn-outline-secondary">
                  <i class="bi bi-pencil"></i> Edit
                </a>
              </div>
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
      else
      {
        <i class="bi bi-box-arrow-in-right icon-xl icon-primary"></i>
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

## Migration from Inline Styles

When updating existing pages:

1. **Identify inline styles**: Search for `style="..."`
2. **Find equivalent class**: Check this guide for component patterns
3. **Replace**: Use component class or create utility class if needed
4. **Test**: Verify visual appearance matches original

### Common Replacements

| Inline Style | Replace With |
|--------------|--------------|
| `style="max-width: 420px"` | `class="auth-card"` or `class="auth-container"` |
| `style="font-size: 3rem"` | `class="icon-xl"` (for icons) |
| `style="text-align: center"` | `class="text-center"` (Bootstrap utility) |
| `style="margin-bottom: 1.5rem"` | `class="mb-lg"` or use Bootstrap `mb-3` |
| `style="display: flex; gap: 0.5rem"` | `class="action-buttons"` or `class="d-flex gap-sm"` |

## Support

Questions? Check:

- This quickstart guide
- `design-system.css` source for available tokens and classes
- Existing pages for usage examples
- Bootstrap 5 documentation for utility classes
