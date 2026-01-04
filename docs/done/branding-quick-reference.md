# Tenant Branding - Quick Reference

**Feature:** Per-tenant branding (logo, colors)  
**Phase:** 2  
**Status:** ✅ Implemented  
**Available:** Multi-tenant mode only

---

## Admin UI

**Route:** `/t/{tenantSlug}/admin/branding`

**Fields:**
- **Logo URL** - Public URL to logo image (PNG, SVG, JPG)
  - Recommended: 200x60px
  - Max length: 200 characters
  - Validation: Valid URL format
  - Optional
  
- **Primary Color** - Main brand color for buttons/accents
  - Format: `#RRGGBB` or `#RGB`
  - Max length: 50 characters
  - Default: `#007bff` (Bootstrap primary)
  - Example: `#FF5733`
  
- **Accent Color** - Secondary color for links/highlights
  - Format: `#RRGGBB` or `#RGB`
  - Max length: 50 characters
  - Default: `#6c757d` (Bootstrap gray)
  - Example: `#33C1FF`

**Features:**
- 🎨 Color pickers with live sync to text fields
- 👁️ Live preview panel showing how branding will look
- 💾 Instant save with success confirmation
- ⚠️ Validation errors shown inline

---

## Service Layer

### Interface

```csharp
public interface ITenantBrandingService
{
    Task<TenantBranding?> GetBrandingAsync(Guid tenantId);
    Task<TenantBranding> GetCurrentTenantBrandingAsync();
}
```

### Model

```csharp
public class TenantBranding
{
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? AccentColor { get; set; }
    public string TenantName { get; set; }
    
    public string GetPrimaryColorOrDefault() => PrimaryColor ?? "#007bff";
    public string GetAccentColorOrDefault() => AccentColor ?? "#6c757d";
    public bool HasCustomBranding { get; }
}
```

### Usage

```csharp
// In a Razor Page or controller
@inject ITenantBrandingService BrandingService

@{
    var branding = await BrandingService.GetCurrentTenantBrandingAsync();
}

<div style="background-color: @branding.GetPrimaryColorOrDefault()">
    @if (!string.IsNullOrEmpty(branding.LogoUrl))
    {
        <img src="@branding.LogoUrl" alt="@branding.TenantName Logo" />
    }
</div>
```

---

## Display System

### Automatic Application

Branding is automatically applied via CSS variables injected in `_Layout.cshtml`:

```css
:root {
    --tenant-primary-color: #FF5733;
    --tenant-accent-color: #33C1FF;
}
```

### Affected Elements

- **Buttons:** `.btn-primary` uses primary color
- **Links:** `<a>` (not nav/buttons) use accent color
- **Badges:** `.badge.bg-primary` uses primary color
- **Navbar:** Brand icon uses primary color
- **Logo:** Shows in navbar if LogoUrl is set

### Hover Effects

Hover states automatically darken using `color-mix(in srgb, var(--color) 85%, black)`.

---

## Database

### Tenant Entity Fields

```csharp
[MaxLength(200)]
public string? LogoUrl { get; set; }

[MaxLength(50)]
public string? PrimaryColor { get; set; }

[MaxLength(50)]
public string? AccentColor { get; set; }
```

### Migration

No migration needed - fields added in Phase 1 (tenant foundation).

---

## API (Future)

Not yet implemented. For Phase 4 consideration:

```
GET  /api/tenants/{id}/branding
PUT  /api/tenants/{id}/branding
```

---

## Configuration

### Enable Multi-Tenancy

Required for branding feature:

```json
{
  "MultiTenancy": {
    "Enabled": true
  }
}
```

### Single-Tenant Mode

Branding UI is hidden. Default branding applies system-wide.

---

## Testing Checklist

### Manual Test Steps

1. ✅ Enable multi-tenancy
2. ✅ Navigate to `/t/default/admin/branding`
3. ✅ Set primary color (e.g., `#FF5733`)
4. ✅ Verify live preview updates
5. ✅ Set accent color (e.g., `#33C1FF`)
6. ✅ Verify live preview updates
7. ✅ Enter logo URL (e.g., `https://via.placeholder.com/200x60`)
8. ✅ Verify logo preview loads
9. ✅ Save changes
10. ✅ Navigate to other pages
11. ✅ Verify buttons use custom primary color
12. ✅ Verify links use custom accent color
13. ✅ Verify logo appears in navbar
14. ✅ Test with invalid URL - see error
15. ✅ Test with invalid hex color - see error
16. ✅ Create second tenant - verify isolation

### Automated Tests

331 tests passing (no branding-specific tests yet).

---

## Common Issues & Solutions

### Logo Not Showing

**Issue:** Logo URL entered but image not displaying  
**Causes:**
- URL is invalid or points to broken link
- Image blocked by CORS policy
- Image host is down

**Solution:**
- Use a reliable CDN
- Verify URL in browser first
- Check browser console for errors

### Colors Not Applying

**Issue:** Custom colors saved but not visible  
**Causes:**
- Browser cache
- CSS specificity conflict
- Single-tenant mode active

**Solution:**
- Hard refresh (Ctrl+Shift+R)
- Verify multi-tenancy enabled
- Check browser dev tools for CSS variable values

### Branding Link Missing

**Issue:** Can't find branding option in admin menu  
**Cause:** Single-tenant mode active  
**Solution:** Enable multi-tenancy in configuration

---

## Limitations & Future Enhancements

### Current Limitations

- ❌ Logo must be externally hosted (no upload)
- ❌ Only 2 colors customizable
- ❌ No dark mode variant support
- ❌ No font customization
- ❌ No email template branding

### Planned Enhancements (Phase 3+)

- 📋 Logo upload to blob storage
- 📋 Extended color palette (error, warning, success)
- 📋 Custom CSS injection
- 📋 Font selection (Google Fonts)
- 📋 Email template branding
- 📋 Dark/light mode variants
- 📋 Brand guidelines enforcement
- 📋 Full-page preview mode

---

## Security Notes

- ✅ URL validation enforced
- ✅ Hex color format validated
- ✅ Max length constraints
- ✅ Tenant isolation (no cross-tenant access)
- ✅ Razor auto-escapes HTML/JS
- ⚠️ Logo URL not validated for HTTPS (HTTP allowed)
- ⚠️ No CSP policy for logo domains (Phase 5)

---

## Performance Notes

- EF Core uses Select projection (no full entity load)
- CSS only injected when custom branding exists
- Logo loading is client-side (browser cached)
- No server-side caching yet (consider Redis in Phase 5)

---

## Related Documentation

- `docs/phase2-branding-implementation.md` - Full implementation details
- `docs/multitenancy-backlog.md` - Phase 2 progress tracking
- `docs/admin-guide.md` - Admin UI documentation (update needed)

---

**Last Updated:** October 8, 2025  
**Author:** GitHub Copilot  
**Version:** 1.0 (Phase 2, Step 1 Complete)
