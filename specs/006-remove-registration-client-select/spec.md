# Feature Specification: Remove Client Selection from User Registration

**Feature Branch**: `006-remove-registration-client-select`  
**Created**: 2024-12-02  
**Status**: Draft  
**Input**: User description: "Fix new user registration by removing client selection dropdown that exposes database records to unauthenticated users. Registration should use default tenant when tenant path is not specified."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Register Without Client Selection (Priority: P1)

A new user navigates to the registration page to create an account. The registration form no longer displays a client selection dropdown, reducing visual complexity and eliminating exposure of internal database records. The user provides their email, optional name, and optional password, then submits the registration.

**Why this priority**: This is the core fix that eliminates the security concern of exposing database records to unauthenticated users. It directly addresses the main issue.

**Independent Test**: Can be fully tested by navigating to the registration page and verifying no client dropdown is visible, then completing a registration successfully.

**Acceptance Scenarios**:

1. **Given** an unauthenticated user on the registration page, **When** the page loads, **Then** no client selection dropdown is displayed.
2. **Given** an unauthenticated user fills in email and submits registration, **When** the form is submitted, **Then** the registration is created without any client association.
3. **Given** a registration is created without client association, **When** an admin later assigns the user to clients, **Then** the user can access those clients normally.

---

### User Story 2 - Registration Uses Tenant from URL Path (Priority: P1)

When a tenant-specific URL path is used (e.g., `/t/{tenant-slug}/Registrations`), the registration is automatically associated with that tenant. This supports multi-tenant isolation without requiring users to select anything.

**Why this priority**: Essential for multi-tenant functionality. Users registering via a tenant-specific URL should be associated with that tenant automatically.

**Independent Test**: Navigate to a tenant-specific registration URL and verify the registration is created under that tenant.

**Acceptance Scenarios**:

1. **Given** a user navigates to `/t/mycompany/Registrations`, **When** they complete registration, **Then** the registration record is associated with the "mycompany" tenant.
2. **Given** a user navigates to the root `/Registrations` without a tenant path, **When** they complete registration, **Then** the registration is associated with the default tenant.

---

### User Story 3 - Self-Service Tenant Creation Remains Functional (Priority: P2)

Users who want to create their own organization can still use the "Create new tenant and become admin" option during registration. This flow remains unchanged and does not require client selection.

**Why this priority**: Important for organizations that want to self-onboard, but secondary to the core registration fix.

**Independent Test**: Complete a registration with the "Create new tenant" option checked and verify tenant and admin role are created.

**Acceptance Scenarios**:

1. **Given** a user checks "Create new tenant and become admin", **When** they provide tenant slug/name and submit, **Then** a new tenant is created and the user becomes its admin.
2. **Given** a user creates a new tenant during registration, **When** the registration is approved, **Then** the user is assigned the tenant-admin role for that tenant.

---

### Edge Cases

- What happens when a user navigates to a non-existent tenant path for registration?
  - System should show an error or redirect to default tenant registration with appropriate messaging.
- What happens when the default tenant is not configured?
  - System should fail gracefully with a clear error message indicating configuration is required.
- What happens to existing registrations that have client associations?
  - Existing data remains unchanged; only new registrations are affected.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST NOT display a client selection dropdown on the user registration page.
- **FR-002**: System MUST NOT expose any client database records to unauthenticated users on the registration page.
- **FR-003**: System MUST determine tenant context from the URL path (e.g., `/t/{slug}/Registrations`) when present.
- **FR-004**: System MUST use the default tenant for registrations when no tenant path is specified in the URL.
- **FR-005**: System MUST allow optional tenant creation during registration (existing functionality preserved).
- **FR-006**: System MUST continue to support optional password field during registration.
- **FR-007**: System MUST continue to support optional first name and last name fields during registration.
- **FR-008**: System MUST NOT require client association for user registration to succeed.
- **FR-009**: System MUST allow administrators to assign users to clients after registration through existing admin interfaces.

### Key Entities

- **Registration**: Represents a pending or processed user registration request. The `ClientId` field becomes unused for new registrations but may retain data for historical records.
- **Tenant**: The organizational boundary for user accounts. Registrations are associated with a tenant based on URL path or default configuration.
- **User**: The account created upon registration approval. Users can be assigned to clients post-registration by administrators.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Registration page loads without displaying any client-specific information to unauthenticated users.
- **SC-002**: 100% of new registrations complete successfully without requiring client selection.
- **SC-003**: Users registering via tenant-specific URLs are correctly associated with the specified tenant.
- **SC-004**: Users registering via root URL are correctly associated with the default tenant.
- **SC-005**: Existing tenant creation functionality during registration continues to work without regression.
- **SC-006**: Registration form submission time is not increased (no additional lookups for clients).

## Assumptions

- A default tenant exists and is configured in the system for non-tenant-path registrations.
- The `ClientId` field on the `Registration` entity will be retained for backward compatibility but not used for new registrations.
- Administrators will use existing admin UI to assign users to clients after registration when needed.
- The tenant path middleware already correctly extracts tenant context from URLs like `/t/{slug}/`.

