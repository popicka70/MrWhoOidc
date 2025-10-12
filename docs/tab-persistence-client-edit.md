# Tab Persistence in Client Edit Page

**Implementation Date:** October 12, 2025  
**Feature:** URL-based tab persistence for Client Edit page

## Problem

When tenant admins edited client settings (especially adding/removing scopes or providers), the page would refresh and always return to the first tab (General). This created a poor user experience as users lost their context and had to manually navigate back to the tab they were working on.

## Solution

Implemented URL query parameter-based tab persistence that:
1. Preserves the active tab in the URL
2. Restores the correct tab after page reload
3. Updates the URL when users manually switch tabs

## Implementation Details

### Backend Changes

**File:** `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`

Added query string parameter binding:
```csharp
[FromQuery(Name = "tab")]
public string? ActiveTab { get; set; }
```

Updated POST handlers to include tab parameter in redirects:
- `OnPostAddScopeAsync()` → redirects with `?tab=scopes`
- `OnPostRemoveScopeAsync()` → redirects with `?tab=scopes`
- `OnPostAddProviderAsync()` → redirects with `?tab=providers`
- `OnPostDeleteProviderAsync()` → redirects with `?tab=providers`

### Frontend Changes

**File:** `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`

Added JavaScript in the `@section Scripts` block to:

1. **Restore tab on page load:**
   ```javascript
   const urlParams = new URLSearchParams(window.location.search);
   const tabParam = urlParams.get('tab');
   
   if (tabParam) {
       const tabButton = document.getElementById(tabParam + '-tab');
       if (tabButton) {
           const tab = new bootstrap.Tab(tabButton);
           tab.show();
       }
   }
   ```

2. **Update URL when user switches tabs:**
   ```javascript
   const tabButtons = document.querySelectorAll('button[data-bs-toggle="tab"]');
   tabButtons.forEach(button => {
       button.addEventListener('shown.bs.tab', function(event) {
           const targetId = event.target.id;
           const tabName = targetId.replace('-tab', '');
           
           const newUrl = new URL(window.location);
           newUrl.searchParams.set('tab', tabName);
           window.history.replaceState({}, '', newUrl);
       });
   });
   ```

## Available Tab Names

Tab parameter values correspond to tab IDs:
- `general` - General settings
- `redirect-uris` - Redirect URIs
- `providers` - Identity Providers
- `scopes` - OAuth2 Scopes
- `keys` - Client Keys (JWKS)
- `introspection` - Introspection settings
- `m2m` - Machine-to-Machine settings
- `obo` - On-Behalf-Of settings
- `tools` - Developer Tools

## Usage Examples

### Direct URL Navigation
```
https://localhost:8443/Admin/Clients/Edit/{guid}?tab=scopes
https://localhost:8443/Admin/Clients/Edit/{guid}?tab=providers
https://localhost:8443/t/acme/Admin/Clients/Edit/{guid}?tab=keys
```

### User Flow
1. User navigates to client edit page
2. User clicks "Scopes" tab
3. URL updates to: `.../Edit/{guid}?tab=scopes`
4. User clicks "Add" to assign a scope
5. Page refreshes with same URL
6. JavaScript detects `?tab=scopes` and restores Scopes tab
7. User sees their scope added without losing context

## Technical Notes

### URL Update Strategy
- Uses `window.history.replaceState()` instead of `pushState()` to avoid polluting browser history
- No page reload when manually switching tabs
- Clean URL updates without hash fragments

### Bootstrap 5 Integration
- Uses Bootstrap 5 Tab API: `new bootstrap.Tab(element).show()`
- Listens to `shown.bs.tab` event for tab changes
- Compatible with Bootstrap's data attributes

### Fallback Behavior
- If invalid tab name provided, defaults to first tab (General)
- If no tab parameter, shows General tab (Bootstrap default)
- Gracefully handles missing tab elements

## Testing

### Manual Testing Steps
1. Navigate to any client edit page
2. Click "Scopes" tab
3. Verify URL shows `?tab=scopes`
4. Add a scope
5. Verify page returns to Scopes tab after refresh
6. Manually click different tabs
7. Verify URL updates for each tab
8. Refresh browser
9. Verify correct tab is restored

### Edge Cases Tested
- ✅ Invalid tab name in URL (falls back to default)
- ✅ No tab parameter (shows General tab)
- ✅ Tab switching without form submission
- ✅ Browser back/forward navigation
- ✅ Multi-tenant URLs with tenant prefix

## Future Enhancements

Possible improvements:
1. **Session storage fallback:** Store last active tab in sessionStorage for non-URL persistence
2. **Animation smoothing:** Add transition delays to avoid jarring tab switches
3. **Deep linking validation:** Server-side validation of tab parameter
4. **Analytics tracking:** Track which tabs users spend most time on
5. **Tab-specific validation:** Show validation errors on correct tab

## Related Files

- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs` - Page model
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml` - Razor view with JavaScript
- `MrWhoOidc.WebAuth/Pages/TenantAwarePageModel.cs` - Base page model with tenant support

## Browser Compatibility

- ✅ Chrome/Edge 90+
- ✅ Firefox 88+
- ✅ Safari 14+
- ✅ All browsers supporting `URLSearchParams` and `History.replaceState`

## Benefits

1. **Improved UX:** Users don't lose context when performing actions
2. **Bookmarkable:** Users can bookmark specific tabs
3. **Shareable:** Support teams can share direct links to specific tabs
4. **Intuitive:** URL reflects current page state
5. **No cookies:** No server-side session state required
6. **Fast:** No additional HTTP requests
