# Mobile Responsive - Quick Visual Guide

## Before & After Comparison

### Desktop View (≥768px)
**No changes** - Desktop experience remains identical to before.

### Mobile View (<768px)

#### Navigation Changes

**BEFORE:**
```
┌─────────────────────────┐
│ MrWhoOidc.WebAuth       │
│ Register | Pass | TOTP  │
│ Hello, User | Logout    │
└─────────────────────────┘
┌─────────────────────────┐
│ ▸ Home                  │
│ ▸ OIDC Discovery        │
│ ▸ JWKS                  │
│ ADMIN                   │
│ ▸ Realms                │
│ ▸ Clients               │
│ ▸ Providers             │
│ ▸ Provider mappings     │
│ ▸ Scopes                │
│ ▸ Roles                 │
│ ▸ Users                 │
│ ▸ Registrations         │
│ ▸ BCL outbox            │
└─────────────────────────┘
┌─────────────────────────┐
│                         │
│  CONTENT PUSHED WAY     │
│  DOWN HERE - NOT        │
│  VISIBLE WITHOUT        │
│  SCROLLING PAST ALL     │
│  NAVIGATION             │
│                         │
└─────────────────────────┘
```

**AFTER:**
```
┌─────────────────────────┐
│ ☰  MrWhoOidc.WebAuth  👤│ ← Hamburger + User dropdown
└─────────────────────────┘
┌─────────────────────────┐
│ ✅ Clients              │ ← Content immediately visible!
│ ┌─────────────────────┐ │
│ │ 🔑 Client ID        │ │
│ │ ├ ClientID: abc123  │ │
│ │ │                   │ │
│ │ 🏷️ Name             │ │
│ │ ├ My Client App     │ │
│ │                     │ │
│ │ [Edit] [Delete]     │ │ ← Full-width buttons
│ └─────────────────────┘ │
└─────────────────────────┘

Tap ☰ to open sidebar overlay
```

#### Sidebar Behavior

**Hamburger Tapped:**
```
┌───────────────┬─────────┐
│ Navigation  ✕ │         │ ← Offcanvas overlay
│               │         │
│ ▸ Home        │ Content │
│ ▸ Discovery   │ dimmed  │
│ ▸ JWKS        │ behind  │
│ ADMIN         │         │
│ ▸ Realms      │         │
│ ▸ Clients     │         │
│ ▸ Providers   │         │
│ ...           │         │
└───────────────┴─────────┘
```

#### Table View Transformation

**BEFORE (Desktop table on mobile = horizontal scroll nightmare):**
```
┌───────────────────────────────────────────────────→
│ Client ID │ Name │ Realm │ PKCE │ Consent │ Status │ Actions
├───────────────────────────────────────────────────→
│ abc123    │ My   │ defau │ Yes  │ Yes     │ JWKS   │ Edit Del
│ xyz789    │ Test │ test  │ No   │ No      │ no     │ Edit Del
└───────────────────────────────────────────────────→
    ⚠️ User must scroll horizontally - BAD UX
```

**AFTER (Card-based layout):**
```
┌─────────────────────────┐
│ 🔑 Client ID           │
│    abc123              │
│                        │
│ 🏷️ Name               │
│    My Client App       │
│                        │
│ ── Actions ──          │
│ [      Edit      ]     │ ← Touch-friendly
│ [     Delete     ]     │    48px height
└─────────────────────────┘

┌─────────────────────────┐
│ 🔑 Client ID           │
│    xyz789              │
│                        │
│ 🏷️ Name               │
│    Test Client         │
│                        │
│ ── Actions ──          │
│ [      Edit      ]     │
│ [     Delete     ]     │
└─────────────────────────┘
```

#### User Menu Dropdown

**User Icon Tapped:**
```
┌─────────────────────────┐
│ ☰  MrWhoOidc.WebAuth  👤│
└────────────────────┬────┘
                     │
        ┌────────────▼────────┐
        │ System Administrator│
        ├─────────────────────┤
        │ 👤 Register         │
        │ 🔑 Change password  │
        │ 🛡️ Two-factor (TOTP)│
        ├─────────────────────┤
        │ ⎋  Log out          │
        └─────────────────────┘
```

## Key Improvements

### 1. Immediate Content Access
- ✅ Main content visible without scrolling
- ✅ Sidebar hidden until needed
- ✅ No vertical navigation blocking content

### 2. Touch-Friendly Buttons
- ✅ 48px minimum height
- ✅ Full-width on mobile
- ✅ Proper spacing (no fat-finger errors)

### 3. Smart Column Hiding
Mobile shows essential columns only:
- **Clients**: ID + Name
- **Users**: Username + Email
- **Realms**: Name + Display Name
- **Roles**: Name only

Desktop shows all columns as before.

### 4. Responsive Breakpoints

| Screen Size | Layout | Sidebar | Tables |
|-------------|--------|---------|--------|
| <768px (Mobile) | 1 column | Offcanvas | Cards |
| 768-991px (Tablet) | 2 columns | Visible | Table (fewer cols) |
| ≥992px (Desktop) | 3 columns | Visible | Table (all cols) |

## Testing on Your Phone

1. Build and run the app:
   ```bash
   dotnet run --project MrWhoOidc.AppHost
   ```

2. Find the WebAuth URL in console output (e.g., `https://localhost:7208`)

3. On your phone's browser:
   - Navigate to the URL
   - Accept the self-signed cert warning
   - Log in as admin
   - Tap the hamburger menu (☰)
   - Navigate to Admin → Clients
   - Observe the card layout
   - Tap Edit/Delete buttons (should be easy to tap)

4. Check these scenarios:
   - [ ] Can see Clients list immediately (no scrolling past nav)
   - [ ] Hamburger opens/closes sidebar smoothly
   - [ ] Can tap all buttons without zooming
   - [ ] No horizontal scrolling anywhere
   - [ ] User menu dropdown works
   - [ ] All admin pages are usable

## CSS Classes Reference

For developers extending the mobile UI:

### Hide columns on specific breakpoints
```html
<th class="d-none d-md-table-cell">Visible on tablets+</th>
<th class="d-none d-lg-table-cell">Visible on desktops only</th>
```

### Enable card transformation
```html
<div class="table-responsive table-responsive-cards">
  <table class="table">
    <tbody>
      <tr>
        <td data-label="Field Name">Value</td>
        <!-- data-label becomes the label in card view -->
      </tr>
    </tbody>
  </table>
</div>
```

### Touch-friendly button sizing
```html
<button class="btn btn-primary">
  <!-- Automatically 48px minimum height on mobile -->
</button>
```

### Offcanvas sidebar pattern
```html
<aside class="offcanvas-md offcanvas-start" id="sidebarNav">
  <div class="offcanvas-header d-md-none">
    <h5>Navigation</h5>
    <button type="button" class="btn-close" data-bs-dismiss="offcanvas"></button>
  </div>
  <div class="offcanvas-body">
    <!-- Sidebar content -->
  </div>
</aside>
```

## Device-Specific Notes

### iOS Safari
- ✅ Offcanvas animations work smoothly
- ✅ Touch targets meet Apple's 44×44pt guideline
- ✅ No viewport zoom issues

### Android Chrome
- ✅ Hamburger menu responsive
- ✅ Touch ripple effects on buttons
- ✅ Back button closes offcanvas

### iPad/Tablets
- ✅ Shows more columns than phone
- ✅ Sidebar always visible at 768px+
- ✅ Two-column layout for better space usage

## Common Issues & Solutions

### Issue: "Sidebar won't open on mobile"
**Solution**: Ensure Bootstrap JS bundle is loaded:
```html
<script src="~/lib/bootstrap/dist/js/bootstrap.bundle.min.js"></script>
```

### Issue: "Cards not showing, still seeing table"
**Solution**: Check that you added both classes:
```html
<div class="table-responsive table-responsive-cards">
```

### Issue: "Columns still visible on mobile"
**Solution**: Ensure both `th` and `td` have `d-none d-md-table-cell`:
```html
<th class="d-none d-md-table-cell">Column</th>
<td class="d-none d-md-table-cell" data-label="Column">Value</td>
```

### Issue: "Buttons too small to tap"
**Solution**: Remove `btn-sm` class on mobile or let CSS handle it:
```css
@media (max-width: 767.98px) {
  .btn-sm { min-height: 44px; }
}
```

## Performance Tips

- Offcanvas uses CSS transforms (hardware accelerated)
- Card transformation is pure CSS (no JS overhead)
- Media queries are cached by browser
- No additional HTTP requests

## Accessibility

Phase 1 includes:
- ✅ ARIA labels on toggle buttons
- ✅ Proper heading hierarchy
- ✅ Keyboard navigation (Tab, Enter, Escape)
- ✅ Screen reader friendly labels (`data-label` attributes)
- ✅ Focus indicators on interactive elements

Future phases will add:
- Skip navigation links
- Focus trapping in offcanvas
- Voice control commands
