# Settings Page Checkbox Fix

**Date:** October 8, 2025  
**Issue:** `System.InvalidOperationException` when accessing `/t/{tenantSlug}/admin/settings`

## Problem

The Settings page was throwing an exception:
```
System.InvalidOperationException: Unexpected 'asp-for' expression result type 'System.Nullable`1[[System.Boolean]]' 
for <input>. 'asp-for' must be of type 'System.Boolean' or 'System.String' that can be parsed as a 'System.Boolean' 
if 'type' is 'checkbox'.
```

**Root Cause:** ASP.NET Core's `asp-for` tag helper for checkboxes requires non-nullable `bool` or `string` types, but the `SettingsInput` class properties were defined as `bool?` (nullable boolean).

## Solution

Implemented a dual-layer property pattern in `Settings.cshtml.cs`:

1. **Private nullable backing fields** - Store the actual nullable state (`null` = use platform default, `true` = enabled, `false` = disabled but explicitly set)
2. **Public non-nullable properties** - Expose `bool` for Razor page binding
3. **Getter methods** - Return nullable values for storage
4. **Setter methods** - Accept nullable values when loading from database

### Pattern Example

```csharp
private bool? _allowRefreshTokenIntrospection;

// UI binding property (non-nullable for checkboxes)
public bool AllowRefreshTokenIntrospection 
{ 
    get => _allowRefreshTokenIntrospection ?? false;
    set => _allowRefreshTokenIntrospection = value ? true : null;
}

// Method to get nullable value for storage
public bool? GetAllowRefreshTokenIntrospection() => _allowRefreshTokenIntrospection;

// Method to set nullable value from loaded settings
public void SetAllowRefreshTokenIntrospection(bool? value) => _allowRefreshTokenIntrospection = value;
```

### Behavior

- **Checkbox unchecked:** Property returns `false`, setter stores `null` → "Use platform default"
- **Checkbox checked:** Property returns `true`, setter stores `true` → "Override to true"
- **Loading `null` from DB:** Checkbox displays unchecked (falls back to platform default)
- **Loading `true` from DB:** Checkbox displays checked
- **Loading `false` from DB:** Currently treated same as `null` (limitation of checkbox UI)

## Files Modified

- `MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml.cs`
  - Updated `SettingsInput` class with dual-layer property pattern
  - Updated `OnPostAsync()` to use getter methods
  - Updated `PopulateForm()` to use setter methods

## Trade-offs

### Current Implementation
- ✅ Solves the immediate exception
- ✅ Preserves nullable storage in database
- ✅ Simple UI: unchecked = use default, checked = override to true
- ⚠️ Cannot explicitly set a tenant override to `false` (only `true` or `null`)

### Alternative Approach (Not Implemented)
Use tri-state checkboxes (checked/unchecked/indeterminate) with JavaScript:
- ✅ Could represent all three states: `null`, `false`, `true`
- ❌ More complex UX
- ❌ Requires custom JavaScript
- ❌ Less standard UI pattern

## Testing

- **Build:** ✅ All projects compile successfully
- **Unit Tests:** ✅ All 331 tests pass
- **Runtime:** Ready to test in browser

## Next Steps

1. Test the Settings page in browser to confirm it loads without errors
2. Verify that:
   - Unchecked checkboxes save as `null` (use platform default)
   - Checked checkboxes save as `true` (override enabled)
   - Platform defaults display correctly alongside settings
3. Consider if explicit `false` overrides are needed (would require tri-state UI)
