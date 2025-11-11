# Research: WebAuth UI Unification

**Phase**: 0 - Research and Discovery  
**Date**: November 11, 2025  
**Feature**: WebAuth UI Unification

## Overview

This document captures research findings for establishing a unified design system in MrWhoOidc.WebAuth through CSS refactoring and component standardization.

## Research Areas

### 1. CSS Custom Properties (CSS Variables) Best Practices

**Decision**: Use CSS custom properties for all design tokens (colors, spacing, typography, shadows, transitions)

**Rationale**:

- **Browser Support**: CSS custom properties have universal support in modern browsers (>98% globally)
- **Dynamic Theming**: Enables runtime theme switching (including tenant branding overrides)
- **Performance**: No build step required, changes apply instantly
- **Maintainability**: Single source of truth for design values
- **Developer Experience**: IntelliSense support in modern editors, easy to understand
- **Cascade Inheritance**: Can be scoped to specific elements for tenant customization

**Alternatives Considered**:

- **SASS/LESS Variables**: Rejected - Requires build step, adds complexity, no runtime flexibility
- **Hardcoded Values**: Rejected - Unmaintainable, inconsistent, difficult to update globally
- **Tailwind CSS**: Rejected - Too disruptive, would require rewriting all markup, conflicts with Bootstrap

**Implementation Pattern**:

```css
:root {
  /* Color System */
  --color-primary: #0d6efd;
  --color-secondary: #6c757d;
  --color-success: #198754;
  --color-danger: #dc3545;
  --color-warning: #ffc107;
  --color-info: #0dcaf0;
  
  /* Spacing Scale */
  --space-xs: 0.25rem;
  --space-sm: 0.5rem;
  --space-md: 1rem;
  --space-lg: 1.5rem;
  --space-xl: 2rem;
  --space-2xl: 3rem;
  
  /* Typography */
  --font-family-base: "Segoe UI", system-ui, -apple-system, sans-serif;
  --font-size-base: 1rem;
  --font-weight-normal: 400;
  --font-weight-semibold: 600;
  --font-weight-bold: 700;
  
  /* Border Radius */
  --radius-sm: 0.375rem;
  --radius-md: 0.5rem;
  --radius-lg: 0.75rem;
  --radius-xl: 1rem;
  
  /* Shadows */
  --shadow-sm: 0 1px 2px 0 rgba(0, 0, 0, 0.05);
  --shadow-md: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
  --shadow-lg: 0 10px 15px -3px rgba(0, 0, 0, 0.1);
  
  /* Transitions */
  --transition-base: all 0.2s ease;
  --transition-fast: all 0.15s ease;
}

/* Tenant Branding Override Example */
[data-tenant="acme"] {
  --color-primary: #ff6b00;
  --font-family-base: "Inter", sans-serif;
}
```

**References**:

- MDN: [Using CSS Custom Properties](https://developer.mozilla.org/en-US/docs/Web/CSS/Using_CSS_custom_properties)
- CSS Tricks: [A Complete Guide to Custom Properties](https://css-tricks.com/a-complete-guide-to-custom-properties/)

### 2. Design System Component Patterns

**Decision**: Create reusable BEM-style CSS classes for common UI patterns

**Rationale**:

- **BEM Methodology**: Block-Element-Modifier provides clear naming, avoids conflicts, scales well
- **Composability**: Components can be combined without side effects
- **Bootstrap Integration**: Works alongside Bootstrap utilities without conflicts
- **Maintainability**: Clear ownership, easy to locate and update
- **Documentation**: Self-documenting class names (e.g., `.page-header__title`)

**Component Inventory** (extracted from page analysis):

1. **Page Headers**: Icon + title + subtitle + action button(s)
2. **Data Tables**: Responsive tables with filters, action buttons, badges
3. **Form Groups**: Label + input + validation message patterns
4. **Alert Messages**: Success/error/warning/info with icons and dismiss
5. **Auth Cards**: Centered cards for login/consent with logo and branding
6. **Action Button Groups**: Consistent spacing and styling for button clusters
7. **Icon Sizing**: Standard icon size classes for consistency

**Alternatives Considered**:

- **Atomic CSS (Tailwind-style)**: Rejected - Too disruptive, conflicts with Bootstrap
- **Component Libraries (React/Vue)**: Rejected - MrWhoOidc uses Razor Pages, not SPA
- **Inline Styles**: Rejected - Current problem, not a solution

**Implementation Pattern**:

```css
/* Page Header Component */
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: var(--space-xl);
}

.page-header__content {
  flex: 1;
}

.page-header__title {
  margin: 0;
  font-size: 2rem;
  font-weight: var(--font-weight-bold);
  display: flex;
  align-items: center;
  gap: var(--space-sm);
}

.page-header__subtitle {
  margin: 0;
  color: var(--color-secondary);
}

.page-header__actions {
  display: flex;
  gap: var(--space-sm);
}

/* Data Table Component */
.data-table {
  width: 100%;
  border-collapse: collapse;
}

.data-table__header {
  background-color: var(--color-light);
  font-weight: var(--font-weight-semibold);
}

.data-table__row:hover {
  background-color: rgba(0, 0, 0, 0.02);
}

/* Auth Card Component */
.auth-card {
  max-width: 420px;
  margin: 0 auto;
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-lg);
  overflow: hidden;
}

.auth-card__header {
  text-align: center;
  padding: var(--space-xl);
}

.auth-card__logo {
  max-width: 120px;
  max-height: 80px;
  object-fit: contain;
}
```

**References**:

- [BEM Methodology](https://getbem.com/)
- [CSS Guidelines by Harry Roberts](https://cssguidelin.es/)

### 3. Inline Style Elimination Strategy

**Decision**: Systematic three-phase replacement approach

**Rationale**:

- **Phase 1 - Static Sizing**: Replace fixed dimensions with CSS classes (e.g., `max-width: 420px` → `.auth-container`)
- **Phase 2 - Color/Spacing**: Replace color and spacing values with variable references
- **Phase 3 - Layout**: Replace flexbox/grid inline styles with utility or component classes

**Retention Policy**: Keep inline styles ONLY for truly dynamic values (e.g., progress bar width, dynamic colors from database)

**Implementation Pattern**:

```html
<!-- BEFORE: Inline styles -->
<div style="max-width: 420px; margin: 0 auto; padding: 2rem;">
  <img src="logo.png" style="max-width: 120px; max-height: 80px; object-fit: contain;" />
  <h2 style="font-size: 1.5rem; font-weight: 700; margin-top: 1rem;">Title</h2>
</div>

<!-- AFTER: CSS classes -->
<div class="auth-container">
  <img src="logo.png" class="auth-logo" />
  <h2 class="auth-title">Title</h2>
</div>
```

**References**:

- [Refactoring UI](https://www.refactoringui.com/) - Design system principles
- [Every Layout](https://every-layout.dev/) - Component-based layout patterns

### 4. Responsive Design Patterns

**Decision**: Mobile-first responsive approach using Bootstrap breakpoints

**Rationale**:

- **Bootstrap Compatibility**: Use Bootstrap's breakpoint system (sm, md, lg, xl, xxl)
- **Mobile First**: Base styles for mobile, progressive enhancement for larger screens
- **Utility Classes**: Leverage Bootstrap responsive utilities (d-none, d-md-block, etc.)
- **Component Adaptation**: Each component class includes responsive behavior

**Breakpoints** (Bootstrap 5):

- `xs`: <576px (default, no prefix)
- `sm`: ≥576px
- `md`: ≥768px
- `lg`: ≥992px
- `xl`: ≥1200px
- `xxl`: ≥1400px

**Implementation Pattern**:

```css
/* Mobile first: base styles */
.page-header {
  flex-direction: column;
  gap: var(--space-md);
}

.page-header__actions {
  width: 100%;
}

/* Tablet and up: horizontal layout */
@media (min-width: 768px) {
  .page-header {
    flex-direction: row;
  }
  
  .page-header__actions {
    width: auto;
  }
}

/* Data table: stack on mobile */
.data-table__cell {
  display: block;
  text-align: left;
  padding: var(--space-sm);
}

.data-table__cell::before {
  content: attr(data-label);
  font-weight: var(--font-weight-semibold);
  margin-right: var(--space-sm);
}

@media (min-width: 768px) {
  .data-table__cell {
    display: table-cell;
  }
  
  .data-table__cell::before {
    content: none;
  }
}
```

**References**:

- [Bootstrap 5 Breakpoints](https://getbootstrap.com/docs/5.3/layout/breakpoints/)
- [Responsive Tables](https://css-tricks.com/responsive-data-tables/)

### 5. Icon Usage Standardization

**Decision**: Define standard icon size classes and color patterns

**Rationale**:

- **Consistency**: Icons should have predictable sizes across the application
- **Accessibility**: Proper sizing ensures clickable targets meet WCAG standards
- **Visual Hierarchy**: Size communicates importance

**Icon Size Scale**:

```css
.icon-xs { font-size: 1rem; }      /* 16px - inline text */
.icon-sm { font-size: 1.25rem; }   /* 20px - buttons, labels */
.icon-md { font-size: 1.5rem; }    /* 24px - page headers */
.icon-lg { font-size: 2rem; }      /* 32px - decorative */
.icon-xl { font-size: 3rem; }      /* 48px - hero sections */
.icon-2xl { font-size: 4rem; }     /* 64px - empty states */
```

**Color Pattern**:

```css
.icon-primary { color: var(--color-primary); }
.icon-success { color: var(--color-success); }
.icon-danger { color: var(--color-danger); }
.icon-warning { color: var(--color-warning); }
.icon-muted { color: var(--color-secondary); }
```

**Usage**:

```html
<!-- BEFORE -->
<i class="bi bi-person text-primary" style="font-size: 3rem;"></i>

<!-- AFTER -->
<i class="bi bi-person icon-xl icon-primary"></i>
```

### 6. Accessibility Considerations

**Decision**: Maintain WCAG 2.1 AA compliance throughout refactoring

**Requirements**:

- **Color Contrast**: Minimum 4.5:1 for normal text, 3:1 for large text
- **Focus Indicators**: Visible focus states on all interactive elements
- **Touch Targets**: Minimum 44x44px for mobile interactions
- **Semantic HTML**: Proper heading hierarchy, ARIA labels where needed

**Implementation**:

```css
/* Focus states */
.btn:focus,
.form-control:focus {
  outline: 2px solid var(--color-primary);
  outline-offset: 2px;
}

/* High contrast mode support */
@media (prefers-contrast: high) {
  :root {
    --color-primary: #0056b3;
    --shadow-md: 0 0 0 2px rgba(0, 0, 0, 0.2);
  }
}

/* Reduced motion support */
@media (prefers-reduced-motion: reduce) {
  * {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
```

**References**:

- [WCAG 2.1 Guidelines](https://www.w3.org/WAI/WCAG21/quickref/)
- [Accessible Color Contrast](https://webaim.org/resources/contrastchecker/)

## Technology Decisions Summary

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Design Tokens** | CSS Custom Properties | Runtime flexibility, tenant branding support, no build step |
| **Component Naming** | BEM Methodology | Clear ownership, scalable, self-documenting |
| **Responsive Strategy** | Mobile-first with Bootstrap breakpoints | Bootstrap compatibility, progressive enhancement |
| **Icon Standardization** | Size classes + color utilities | Consistency, accessibility, reusability |
| **Inline Style Policy** | Eliminate except dynamic values | Maintainability, consistency, single source of truth |
| **Testing Approach** | Manual visual testing + screenshots | Appropriate for UI refactoring, no behavioral changes |

## Next Steps (Phase 1)

1. Create `design-system.css` with all CSS custom properties and component classes
2. Document component usage patterns in `quickstart.md`
3. Audit all .cshtml files and catalog inline styles
4. Create replacement mapping (inline style → CSS class)
5. Begin systematic refactoring by component type
