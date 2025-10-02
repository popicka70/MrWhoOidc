# Auto-Approval for New User Registrations - Implementation Summary

## Overview

This feature adds client-level control over automatic approval of new user registrations. It allows administrators to configure whether new users coming from external identity providers (and optionally local registrations) should be automatically approved without requiring manual admin intervention.

## Feature Components

### 1. AutoApprovalMode Enum

Located: `MrWhoOidc.Auth/Persistence/AutoApprovalMode.cs`

Three modes are available:
- **No** (default): All registrations require manual admin approval
- **OnlyExternalIdp**: Automatically approve only registrations from external IdP logins
- **All**: Automatically approve all registrations regardless of source (local or external)

### 2. Client Entity Changes

Modified: `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

Added property:
```csharp
public AutoApprovalMode AutoApprovalMode { get; set; } = AutoApprovalMode.No;
```

### 3. Database Migration

Created: `MrWhoOidc.Auth/Persistence/Migrations/20251002160659_AddAutoApprovalModeToClient.cs`

Adds the `AutoApprovalMode` column to the `Clients` table with default value of `0` (No).

### 4. Registration Service

Created: `MrWhoOidc.WebAuth/Services/RegistrationService.cs`

New service that centralizes registration creation and approval logic:
- `CreateAndMaybeApproveRegistrationAsync`: Creates a registration and optionally auto-approves it
- `ApproveRegistrationAsync`: Approves a pending registration and creates the user account

This service is reusable for both manual admin approval workflows and automatic approval flows.

### 5. External OIDC User Provisioner Updates

Modified: `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcUserProvisioner.cs`

Enhanced the auto-provisioning logic to:
1. Check the client's `AutoApprovalMode` setting
2. If auto-approval is enabled for external IdP:
   - Create a registration record
   - Immediately approve it using `RegistrationService`
   - Link the external identity to the newly created user
3. Fall back to standard auto-provisioning if auto-approval fails

### 6. Admin UI Updates

Modified files:
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`
- `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml`

Added:
- `AutoApprovalMode` property to `ClientInput` model
- Dropdown selector in the UI with explanatory help text
- Proper mapping between the input model and the Client entity

### 7. Dependency Injection

Modified: `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcServiceCollectionExtensions.cs`

Registered `IRegistrationService` as a scoped service.

## User Flow

### External IdP Registration with Auto-Approval (OnlyExternalIdp mode)

1. User attempts to sign in via external IdP (e.g., Google, Azure AD)
2. External IdP authenticates the user successfully
3. The system detects this is a new user (no existing ExternalIdentity record)
4. System checks client's `AutoApprovalMode`:
   - If `OnlyExternalIdp` or `All`: Creates registration and auto-approves it
   - If `No`: Falls back to standard provisioning (or policy denial based on `AllowExternalAutoProvision`)
5. For auto-approval:
   - Registration record is created with state "pending"
   - Immediately approved (state → "approved")
   - User account is created
   - External identity is linked
   - User is signed in automatically
6. Audit trail: Registration record shows when it was created and approved (ApprovedAt, ApprovedByUserId will be null for auto-approval)

### Manual Admin Approval Flow (No mode - existing behavior)

1. User submits registration via `/registrations`
2. Registration is created with state "pending"
3. Admin reviews pending registrations in admin panel
4. Admin clicks "Approve" or "Reject"
5. On approval, `RegistrationService.ApproveRegistrationAsync` is called
6. User account is created and assigned to client/realm

## Configuration

Admins configure auto-approval per client in the admin UI:

1. Navigate to **Admin → Clients → Edit [ClientName]**
2. Find the **"User Registration Auto-Approval"** card
3. Select the desired mode from the dropdown:
   - **No**: Manual approval required (safest, default)
   - **OnlyExternalIdp**: Auto-approve external IdP users only
   - **All**: Auto-approve all new registrations
4. Save changes

## Security Considerations

1. **Default is safest**: New clients default to `No` (manual approval required)
2. **External IdP trust**: `OnlyExternalIdp` assumes external IdP has verified the user's identity
3. **Audit trail**: All registrations (auto-approved or not) create a `Registration` record
4. **Email validation**: Auto-approval still validates email addresses and prevents duplicates
5. **Client assignment**: Auto-approved users are automatically assigned to the registering client

## Testing Recommendations

1. **Test auto-approval for external IdP**:
   - Configure a test client with `AutoApprovalMode = OnlyExternalIdp`
   - Sign in with a new user from an external IdP
   - Verify user is created and signed in automatically
   - Check registration record shows "approved" state

2. **Test manual approval still works**:
   - Configure client with `AutoApprovalMode = No`
   - Attempt external IdP sign-in with new user
   - Verify registration remains pending
   - Manually approve via admin UI

3. **Test `All` mode**:
   - Configure client with `AutoApprovalMode = All`
   - Test both local registration and external IdP
   - Verify both are auto-approved

4. **Test failure scenarios**:
   - Duplicate email addresses
   - Invalid email addresses
   - Existing user accounts

## Migration Notes

- Existing clients will have `AutoApprovalMode = No` after migration (safe default)
- No behavior changes for existing deployments until admins explicitly change client settings
- The feature is backward compatible with existing registration flows
- Database migration adds a single integer column (no performance impact)

## Future Enhancements (Optional)

1. **Confirmation UI for new accounts**: Show a "Create new account?" page before auto-approving
2. **Email verification before auto-approval**: Require email confirmation step
3. **Rate limiting**: Prevent abuse of auto-approval for external IdP registrations
4. **Admin notifications**: Notify admins when auto-approval occurs
5. **Conditional approval rules**: Based on email domain, IdP provider, or other claims
