# Client Secret Deprecation UI Enhancement

**Date**: October 17, 2025  
**Status**: Implemented  
**Related**: `docs/client-secret-rotation-backlog.md` Task 4.1.3

---

## Overview

Added a prominent deprecation warning banner to the Secrets tab inside the client editor (`/Admin/Clients/Edit/{id}?tab=secrets`) that displays when a client is still using the legacy single `ClientSecretHash` field instead of the new multi-secret system.

## Implementation Details

### Backend Changes

**File**: `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`

### Frontend Changes

**File**: `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`

Added conditional warning banner within the `#tab-secrets` pane:

```razor
@if (Model.HasLegacyClientSecretHash)
{
   <div class="alert alert-warning alert-dismissible fade show" role="alert">
      <h5 class="alert-heading">
         <i class="bi bi-exclamation-triangle-fill me-2"></i>
         <span class="badge bg-warning text-dark me-2">DEPRECATED</span>
         Legacy Single Secret Detected
      </h5>
      <p class="mb-2">
         This client is using the deprecated single <code>ClientSecretHash</code> field.
         While it will continue to work for backward compatibility, we recommend migrating...
      </p>
      <ul class="mb-2">
         <li><strong>Zero-downtime rotation</strong> — overlap old and new secrets</li>
         <li><strong>Expiry enforcement</strong> — automatically reject expired secrets</li>
         <li><strong>Better audit trail</strong> — track lifecycle events</li>
         <li><strong>Enhanced security</strong> — up to 3 active secrets</li>
      </ul>
      <p class="mb-0">
         <strong>Action:</strong> Generate a new secret below, test it, then the legacy secret will be automatically ignored once you have active secrets.
      </p>
      <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
   </div>
}
```

## Visual Design

### Warning Banner Components

1. **Header**:
   - Yellow warning triangle icon (Bootstrap Icons `bi-exclamation-triangle-fill`)
   - "DEPRECATED" badge with warning styling
   - Clear title: "Legacy Single Secret Detected"

2. **Content**:
   - Explanation of the issue
   - Bulleted list of migration benefits (4 key advantages)
   - Action-oriented guidance for next steps

3. **Styling**:
   - Bootstrap `alert-warning` class (yellow background)
   - Fade-in animation for better UX
   - Professional spacing with margin utilities

## User Experience Flow

### Before Migration

1. Admin opens the client editor and switches to the Secrets tab
2. Yellow deprecation banner displays prominently at top
3. User understands:
   - What the issue is (legacy secret)
   - Why they should migrate (4 clear benefits)
   - How to migrate (generate new secret → test → activate)

### During Migration

1. User clicks "Generate New Secret" button
2. Creates and tests new secret alongside legacy secret
3. Both secrets work simultaneously (zero downtime)

### After Migration

1. Once at least one `ClientSecret` is active, `ClientStore.ValidateClientSecretAsync` prioritizes new secrets
2. Legacy `ClientSecretHash` is functionally ignored (but not deleted for rollback)

## Backward Compatibility

- **No Breaking Changes**: Legacy clients continue to authenticate
- **Graceful Degradation**: If new secret validation fails, falls back to legacy hash
- **Audit Trail**: Migration doesn't erase original secret hash (recovery option)
- Once at least one `ClientSecret` is active, `ClientStore.ValidateClientSecretAsync` prioritizes new secrets

## Testing

### Manual Testing Checklist

- [ ] View secrets tab for client with only `ClientSecretHash` → Banner displays
- [ ] View secrets tab for client with `ClientSecrets` → No banner
- [ ] Dismiss banner → Stays dismissed during session (Bootstrap behavior)
- [ ] Verify warning text is clear and actionable
- [ ] Verify badge and icons render correctly
- [ ] Test responsiveness on mobile viewport

### Automated Testing

No unit tests added for UI rendering logic. Consider E2E tests with Playwright/Selenium if implementing comprehensive UI test suite.

## Future Enhancements

1. **Migration Wizard**: Add "Migrate Now" button that auto-creates ClientSecret from ClientSecretHash
2. **Progress Indicator**: Show migration status (e.g., "2 of 15 clients migrated")
3. **Dashboard Widget**: Display count of legacy clients on admin dashboard
4. **Email Notifications**: Alert tenant admins when legacy secrets detected
5. **Automatic Cleanup**: After N days, suggest removing `ClientSecretHash` column entirely

## Related Files

- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (Client entity with obsolete ClientSecretHash)
- `MrWhoOidc.Auth/Services/ClientStore.cs` (validation logic with fallback)

## References

- [Bootstrap 5 Alerts](https://getbootstrap.com/docs/5.3/components/alerts/)
- [Bootstrap Icons](https://icons.getbootstrap.com/)
- Task 4.1.3 in `docs/client-secret-rotation-backlog.md`
