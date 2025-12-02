# Quickstart: Remove Client Selection from User Registration

**Feature**: 006-remove-registration-client-select  
**Date**: 2024-12-02

## Overview

This guide describes the changes made to remove the client selection dropdown from the user registration page, addressing the security concern of exposing database records to unauthenticated users.

## What Changed

### Before

- Registration page displayed a "Client (optional)" dropdown
- Dropdown listed all clients in the current tenant with their realm names
- Unauthenticated users could see client IDs and realm names
- Database query executed on every page load

### After

- No client dropdown on registration page
- No database query for clients during registration
- Users register without client association
- Administrators assign users to clients post-registration via admin UI

## Files Modified

| File | Change |
|------|--------|
| `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml` | Removed client dropdown div |
| `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml.cs` | Removed `ClientOptions`, `LoadClientsAsync()`, `Input.ClientId` |

## User Impact

### For Registering Users

- Simpler registration form (fewer fields)
- No confusion about client selection
- Registration flow unchanged otherwise

### For Administrators

- Users no longer auto-assigned to clients at registration
- Use Admin UI to assign users to clients after approval:
  1. Navigate to Admin → Users
  2. Select the user
  3. Assign to appropriate client(s)

## Multi-Tenant Behavior

| URL | Tenant Association |
|-----|-------------------|
| `/Registrations` | Default tenant |
| `/t/{slug}/Registrations` | Specified tenant (e.g., `/t/acme/Registrations` → "acme" tenant) |

The tenant association is automatic based on URL path. No user action required.

## Testing the Change

### Manual Testing

1. Navigate to `/Registrations` (or `/t/{slug}/Registrations`)
2. Verify no client dropdown is visible
3. Complete registration with email and optional fields
4. Verify registration appears in admin pending list
5. Approve registration
6. Verify user is created without client assignment
7. Assign user to client via admin UI
8. Verify user can access the client

### Automated Tests

Run the test suite to verify no regressions:

```bash
dotnet test
```

## Rollback

If needed, revert the changes by restoring:

1. `ClientOptions` property and `LoadClientsAsync()` method in `Index.cshtml.cs`
2. Client dropdown div in `Index.cshtml`
3. `ClientId` property in `RegistrationInput` class

No database rollback required (schema unchanged).

## Related Documentation

- [spec.md](./spec.md) - Feature specification
- [research.md](./research.md) - Technical research
- [data-model.md](./data-model.md) - Data model analysis
