# Fluent Design - Quick Reference

## ?? Using Fluent Components

### Buttons

```html
<!-- Primary Action -->
<button class="btn-primary">Save Changes</button>

<!-- Secondary Action -->
<button class="btn-secondary">Cancel</button>

<!-- Outline Button -->
<button class="btn-outline-primary">Learn More</button>

<!-- Subtle Button -->
<button class="btn-subtle">Dismiss</button>

<!-- Icon Button -->
<button class="btn-icon"><i class="bi bi-heart"></i></button>

<!-- Button with Icon -->
<button class="btn-primary">
    <i class="bi bi-check"></i> Approve
</button>

<!-- Loading State -->
<button class="btn-primary loading">Processing...</button>

<!-- Button Sizes -->
<button class="btn-primary btn-sm">Small</button>
<button class="btn-primary">Normal</button>
<button class="btn-primary btn-lg">Large</button>

<!-- Full Width -->
<button class="btn-primary w-100">Submit</button>
```

### Form Controls

```html
<!-- Text Input -->
<div class="form-group">
    <label for="email" class="form-label">Email Address</label>
    <input type="email" id="email" class="form-control" placeholder="you@example.com" />
</div>

<!-- Floating Label -->
<div class="form-floating">
    <input type="text" id="name" class="form-control" placeholder="Name" />
    <label for="name">Full Name</label>
</div>

<!-- Checkbox -->
<div class="form-check">
    <input type="checkbox" id="remember" class="form-check-input" />
    <label for="remember" class="form-check-label">Remember me</label>
</div>

<!-- Radio Buttons -->
<div class="form-check">
    <input type="radio" id="option1" name="options" class="form-check-input" />
    <label for="option1" class="form-check-label">Option 1</label>
</div>

<!-- Toggle Switch -->
<div class="form-switch">
    <input type="checkbox" id="toggle" class="form-check-input" />
    <label for="toggle" class="form-check-label">Enable Feature</label>
</div>

<!-- Select Dropdown -->
<select class="form-select">
    <option>Choose an option</option>
    <option value="1">Option 1</option>
    <option value="2">Option 2</option>
</select>

<!-- Validation States -->
<input type="text" class="form-control is-valid" />
<div class="valid-feedback">Looks good!</div>

<input type="text" class="form-control is-invalid" />
<div class="invalid-feedback">Please provide a valid value.</div>

<!-- Search Input -->
<input type="search" class="form-control" placeholder="Search..." />
```

### Cards

```html
<!-- Basic Card -->
<div class="card">
    <div class="card-body">
        <h5 class="card-title">Card Title</h5>
        <p class="card-text">Card content goes here.</p>
    </div>
</div>

<!-- Card with Header and Footer -->
<div class="card">
    <div class="card-header">Featured</div>
    <div class="card-body">
        <h5 class="card-title">Special title</h5>
        <p class="card-text">Card content.</p>
    </div>
    <div class="card-footer">
        <button class="btn-primary">Action</button>
        <button class="btn-secondary">Cancel</button>
    </div>
</div>

<!-- Interactive Card (clickable) -->
<div class="card card-interactive">
    <div class="card-body">
        <h5 class="card-title">Click Me</h5>
        <p class="card-text">This card has hover effects.</p>
    </div>
</div>

<!-- Card Variants -->
<div class="card card-elevated">...</div> <!-- More shadow -->
<div class="card card-flat">...</div> <!-- No shadow -->
<div class="card card-outlined">...</div> <!-- Border emphasis -->
<div class="card card-subtle">...</div> <!-- Light background -->

<!-- Semantic Cards -->
<div class="card card-primary">...</div>
<div class="card card-success">...</div>
<div class="card card-warning">...</div>
<div class="card card-danger">...</div>

<!-- Card with Image -->
<div class="card">
    <img src="image.jpg" class="card-img-top" alt="..." />
    <div class="card-body">
        <h5 class="card-title">Image Card</h5>
        <p class="card-text">Description text.</p>
    </div>
</div>

<!-- Card Grid -->
<div class="card-grid">
    <div class="card">...</div>
    <div class="card">...</div>
    <div class="card">...</div>
</div>
```

## ?? Utility Classes

### Colors

```html
<!-- Text Colors -->
<span class="text-primary">Primary text</span>
<span class="text-success">Success text</span>
<span class="text-warning">Warning text</span>
<span class="text-error">Error text</span>
<span class="text-muted">Muted text</span>

<!-- Background Colors -->
<div class="bg-primary">Primary background</div>
<div class="bg-success">Success background</div>
<div class="bg-canvas">Canvas background</div>
<div class="bg-card">Card background</div>
```

### Elevation Shadows

```html
<div class="elevation-2">Subtle shadow</div>
<div class="elevation-4">Default card shadow</div>
<div class="elevation-8">Elevated element</div>
<div class="elevation-16">Flyout shadow</div>
<div class="elevation-32">Modal shadow</div>
```

### Border Radius

```html
<div class="rounded-none">No radius</div>
<div class="rounded-sm">Small radius (2px)</div>
<div class="rounded-md">Medium radius (4px)</div>
<div class="rounded-lg">Large radius (8px)</div>
<div class="rounded-xl">Extra large radius (12px)</div>
<div class="rounded-full">Circular</div>
```

### Font Weights

```html
<span class="font-regular">Regular (400)</span>
<span class="font-semibold">Semibold (600)</span>
<span class="font-bold">Bold (700)</span>
```

### Material Effects

```html
<!-- Acrylic (frosted glass) -->
<div class="acrylic">
    Translucent background with blur
</div>

<!-- Mica (subtle shimmer) -->
<div class="mica">
    Subtle animated background
</div>
```

## ?? Design Tokens (CSS Variables)

### Using in Custom CSS

```css
/* Colors */
.my-component {
    color: var(--fluent-color-neutral-foreground-1);
    background-color: var(--fluent-color-card);
    border-color: var(--fluent-color-neutral-stroke-1);
}

/* Spacing */
.my-component {
    padding: var(--fluent-space-16);
    margin-bottom: var(--fluent-space-12);
    gap: var(--fluent-space-8);
}

/* Shadows */
.my-component {
    box-shadow: var(--fluent-shadow-4);
}

.my-component:hover {
    box-shadow: var(--fluent-shadow-8);
}

/* Border Radius */
.my-component {
    border-radius: var(--fluent-border-radius-card);
}

/* Typography */
.my-component {
    font-family: var(--fluent-font-family-base);
    font-size: var(--fluent-font-size-300);
    font-weight: var(--fluent-font-weight-semibold);
}

/* Animations */
.my-component {
    transition: all var(--fluent-duration-normal) var(--fluent-curve-easy-ease);
}
```

## ?? Common Patterns

### Form with Validation

```html
<form>
    <div class="form-group">
        <label for="username" class="form-label required">Username</label>
        <input type="text" id="username" class="form-control is-invalid" />
        <div class="invalid-feedback">Username is required.</div>
    </div>
    
    <div class="form-group">
        <label for="password" class="form-label required">Password</label>
        <input type="password" id="password" class="form-control" />
        <div class="form-text">Must be at least 8 characters.</div>
    </div>
    
    <div class="form-check">
        <input type="checkbox" id="terms" class="form-check-input" />
        <label for="terms" class="form-check-label">I agree to the terms</label>
    </div>
    
    <button type="submit" class="btn-primary w-100">Sign Up</button>
</form>
```

### Card List with Actions

```html
<div class="card-list">
    <div class="card">
        <div class="card-body">
            <h5 class="card-title">Item 1</h5>
            <p class="card-text">Description of item 1</p>
        </div>
        <div class="card-footer">
            <button class="btn-primary btn-sm">Edit</button>
            <button class="btn-outline-danger btn-sm">Delete</button>
        </div>
    </div>
    
    <div class="card">
        <div class="card-body">
            <h5 class="card-title">Item 2</h5>
            <p class="card-text">Description of item 2</p>
        </div>
        <div class="card-footer">
            <button class="btn-primary btn-sm">Edit</button>
            <button class="btn-outline-danger btn-sm">Delete</button>
        </div>
    </div>
</div>
```

### Action Button Group

```html
<div class="btn-group">
    <button class="btn-primary">Save</button>
    <button class="btn-secondary">Cancel</button>
    <button class="btn-subtle">More Options</button>
</div>
```

### Icon Buttons Row

```html
<div style="display: flex; gap: var(--fluent-space-8);">
    <button class="btn-icon"><i class="bi bi-heart"></i></button>
    <button class="btn-icon"><i class="bi bi-share"></i></button>
    <button class="btn-icon"><i class="bi bi-bookmark"></i></button>
    <button class="btn-icon"><i class="bi bi-three-dots"></i></button>
</div>
```

## ?? Responsive Helpers

```html
<!-- Mobile: Stack buttons, Desktop: Inline -->
<div class="btn-group btn-group-mobile-stack">
    <button class="btn-primary">Action 1</button>
    <button class="btn-secondary">Action 2</button>
</div>

<!-- Hide on mobile, show on desktop -->
<div class="d-none d-md-block">Desktop only content</div>

<!-- Show on mobile, hide on desktop -->
<div class="d-md-none">Mobile only content</div>
```

## ?? Dark Mode

```html
<!-- Force dark mode (for testing) -->
<body data-theme="dark">

<!-- Force light mode -->
<body data-theme="light">

<!-- Auto (respects system preference) -->
<body> <!-- No data-theme attribute -->
```

## ?? Debugging Tips

```javascript
// Check current theme
console.log(document.documentElement.getAttribute('data-theme'));

// Get computed color value
const color = getComputedStyle(document.documentElement)
    .getPropertyValue('--fluent-color-brand-primary');
console.log(color); // #0078D4

// Force dark mode via console
document.documentElement.setAttribute('data-theme', 'dark');

// Force light mode via console
document.documentElement.setAttribute('data-theme', 'light');
```

---

**Quick Links:**
- Full documentation: `docs/fluent-design-modernization-plan.md`
- Implementation status: `docs/fluent-implementation-status.md`
- Design tokens: `wwwroot/css/fluent-design-tokens.css`
