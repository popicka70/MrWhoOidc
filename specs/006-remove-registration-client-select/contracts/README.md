# API Contracts: Remove Client Selection from User Registration

**Feature**: 006-remove-registration-client-select  
**Date**: 2024-12-02

## Summary

**No API changes required.** This feature only modifies the Razor Pages UI and does not affect any REST APIs or programmatic interfaces.

## Existing APIs (Unchanged)

### Registration Service Interface

The `IRegistrationService.CreateAndMaybeApproveRegistrationAsync()` method signature remains unchanged:

```csharp
Task<Guid?> CreateAndMaybeApproveRegistrationAsync(
    string email,
    string? firstName,
    string? lastName,
    Guid? clientId,           // ← Remains nullable, UI will pass null
    string? passwordHash,
    bool isExternalIdp,
    bool autoApprove,
    string? tenantSlug = null,
    string? tenantName = null,
    string? tenantDescription = null,
    CancellationToken cancellationToken = default);
```

### Admin APIs

The following admin APIs remain unchanged and continue to support post-registration user management:

- `GET /admin/api/users` - List users
- `POST /admin/api/users/{id}/clients` - Assign user to client
- `GET /admin/api/clients` - List clients (for admin selection)

## Why No API Changes

1. The `clientId` parameter was already nullable
2. Passing `null` was already a valid use case (external IdP provisioning)
3. The approval flow already handles null `ClientId` gracefully
4. Client assignment via admin UI uses existing admin APIs
