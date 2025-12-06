# Research: Remove Client Selection from User Registration

**Feature**: 006-remove-registration-client-select  
**Date**: 2024-12-02  
**Status**: Complete

## Research Summary

This feature is a **simplification** that removes existing code rather than adding new functionality. Research focused on understanding the current implementation and ensuring safe removal.

## Findings

### 1. Current Client Selection Implementation

**Location**: `MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml` and `Index.cshtml.cs`

**Current behavior**:
- `LoadClientsAsync()` queries all clients for the current tenant from the database
- Exposes `ClientOptions` list to the Razor view
- Dropdown allows unauthenticated users to see client IDs and realm names
- Selected `ClientId` is passed to `RegistrationService.CreateAndMaybeApproveRegistrationAsync()`

**Decision**: Remove `ClientOptions`, `LoadClientsAsync()`, and the dropdown UI entirely.

**Rationale**: 
- Exposes database records to unauthenticated users (security concern)
- Not multi-tenant friendly (users shouldn't need to know about clients)
- Client assignment is an admin function, not a user self-service function

**Alternatives considered**:
- Hide dropdown but keep the query → Still executes unnecessary DB query
- Show only client display names → Still exposes business data
- Require authentication first → Changes UX significantly, not requested

---

### 2. Tenant Resolution for Registrations

**Location**: `MrWhoOidc.Auth/MultiTenancy/TenantContext.cs`, tenant resolution middleware

**Current behavior**:
- `ITenantAccessor.CurrentTenant` is populated by middleware from URL path
- Registration page already uses `tenantAccessor.CurrentTenant` to filter clients
- If no tenant path, `CurrentTenant` may be null or default tenant

**Decision**: Continue using existing `ITenantAccessor` pattern. No changes needed.

**Rationale**:
- Tenant resolution already works correctly from URL path
- Default tenant handling is already implemented in the middleware
- Registration service already respects tenant context

**Alternatives considered**:
- Add explicit tenant selection dropdown → Adds complexity, exposes tenant list
- Require tenant in URL always → Breaking change for existing links

---

### 3. ClientId Field on Registration Entity

**Location**: `MrWhoOidc.Auth/Persistence/Registration.cs` (entity)

**Current behavior**:
- `ClientId` is a nullable `Guid?` field
- Stored in database, used during approval to assign user to client
- External IdP registrations may also set this field

**Decision**: Retain the field for backward compatibility; always pass `null` from UI.

**Rationale**:
- Existing registrations with ClientId should continue to work
- External IdP provisioning may still use ClientId
- No schema change required, reducing migration risk

**Alternatives considered**:
- Remove the field → Requires migration, breaks existing data
- Mark as obsolete → Adds noise, field is still valid for programmatic use

---

### 4. Impact on Registration Approval Flow

**Location**: `MrWhoOidc.WebAuth/Services/RegistrationService.cs`

**Current behavior**:
- `ApproveRegistrationAsync()` checks `registration.ClientId`
- If set, creates `UserClientAssignment` for the client
- If null, skips client assignment (user can be assigned later)

**Decision**: No changes needed to approval flow.

**Rationale**:
- Flow already handles null ClientId gracefully
- Client assignment can be done post-registration via admin UI
- Existing FR-009 requirement: "System MUST allow administrators to assign users to clients after registration through existing admin interfaces"

---

### 5. Test Coverage

**Location**: `MrWhoOidc.UnitTests/`

**Current coverage**:
- Registration tests exist but may assume ClientId is provided
- Need to verify tests pass with null ClientId

**Decision**: Add explicit test for registration without ClientId.

**Rationale**:
- Ensures null ClientId path is covered
- Documents expected behavior in test suite
- Validates FR-008: "System MUST NOT require client association for user registration to succeed"

---

## Implementation Checklist

Based on research, the implementation requires:

1. **Remove from `Index.cshtml.cs`**:
   - [ ] Remove `ClientOptions` property
   - [ ] Remove `LoadClientsAsync()` method
   - [ ] Remove calls to `LoadClientsAsync()` in `OnGetAsync` and `OnPostCreateAsync`
   - [ ] Remove `Input.ClientId` from service call (pass `null`)

2. **Remove from `Index.cshtml`**:
   - [ ] Remove client selection dropdown div (lines ~47-55)

3. **Remove from `RegistrationInput` class**:
   - [ ] Remove `ClientId` property

4. **Add unit tests**:
   - [ ] Test registration without client ID succeeds
   - [ ] Test tenant context is preserved from URL path

5. **Verify existing tests**:
   - [ ] Run full test suite to catch regressions

## No Further Research Needed

All technical questions have been resolved through codebase analysis. No external research or clarification required.
