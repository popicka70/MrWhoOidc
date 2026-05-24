# Feature Specification: External IdP Registration

**Feature Branch**: `013-external-idp-registration`  
**Created**: 2025-12-25  
**Status**: Draft  
**Input**: User description: "I'd like to allow users to register using configured external IdP if configured. As of now we have a way to create registration that need to input details. I want to have an alternative on this page to create registration from external IdP that is configured to allow that. So IdPs in default tenant that will have such option turned on will be used to create registrations."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Register via External IdP (Priority: P1)

A new user visits the registration page and sees buttons for configured external identity providers (e.g., Google, Microsoft, corporate SSO) alongside the traditional manual registration form. They click an external IdP button, authenticate with that provider, and their account is automatically created using information from the IdP (email, name, etc.).

**Why this priority**: This is the core feature value—enabling frictionless registration through trusted external providers, reducing user effort and improving conversion rates.

**Independent Test**: Can be fully tested by configuring an external IdP with registration enabled, visiting the registration page, clicking the IdP button, completing external authentication, and verifying a new user account is created with correct information.

**Acceptance Scenarios**:

1. **Given** at least one external IdP is enabled for registration in the default tenant, **When** a user visits the registration page, **Then** the user sees IdP buttons displayed above or alongside the manual registration form.
2. **Given** a user clicks an external IdP registration button, **When** the user completes authentication at the external provider, **Then** a new user account is created using the IdP-provided claims (email, first name, last name).
3. **Given** a user completes external IdP registration, **When** the account is created, **Then** the user is redirected to a success page or auto-logged in based on configuration.

---

### User Story 2 - Admin Enables IdP for Registration (Priority: P2)

An administrator configures an external identity provider and enables a new setting that allows this IdP to be used for user registration on the default tenant's registration page. They can also disable this option for specific IdPs that should only be used for login.

**Why this priority**: Administrators need control over which IdPs appear on the registration page—some providers may only be intended for existing users.

**Independent Test**: Can be tested by accessing the IdP admin configuration, toggling the "Allow registration" setting, and verifying the IdP appears/disappears from the registration page accordingly.

**Acceptance Scenarios**:

1. **Given** an administrator is editing an identity provider configuration, **When** they view the IdP settings, **Then** they see a toggle/checkbox for "Allow registration via this provider."
2. **Given** an administrator enables "Allow registration" for an IdP, **When** a user visits the default tenant registration page, **Then** that IdP button appears in the registration options.
3. **Given** an administrator disables "Allow registration" for an IdP, **When** a user visits the registration page, **Then** that IdP button does not appear in registration options (but may still appear on login page).

---

### User Story 3 - Registration Form Fallback (Priority: P3)

When no external IdPs are configured for registration, or all are disabled, the registration page continues to show only the traditional manual registration form without any visual disruption or errors.

**Why this priority**: Ensures backward compatibility and graceful degradation—the existing registration flow must continue working unchanged when external IdP registration is not configured.

**Independent Test**: Can be tested by disabling all IdPs for registration and verifying the registration page displays only the manual form without errors or empty sections.

**Acceptance Scenarios**:

1. **Given** no external IdPs are enabled for registration in the default tenant, **When** a user visits the registration page, **Then** they see only the traditional manual registration form.
2. **Given** external IdPs exist but none have "Allow registration" enabled, **When** a user visits the registration page, **Then** no IdP buttons are displayed and the manual form works as before.

---

### User Story 4 - Prevent Duplicate Registration (Priority: P3)

When a user attempts to register via an external IdP using an email address that already exists in the system, they receive a clear message explaining that an account already exists and are directed to sign in instead.

**Why this priority**: Prevents confusion and duplicate account creation, maintaining data integrity.

**Independent Test**: Can be tested by registering a user manually, then attempting to register via external IdP with the same email, and verifying an appropriate error message is shown.

**Acceptance Scenarios**:

1. **Given** a user account already exists with email "user@example.com", **When** someone attempts to register via external IdP with the same email, **Then** the system displays a message indicating an account exists and offers a link to sign in.
2. **Given** a user with an existing account attempts IdP registration, **When** they see the account-exists message, **Then** they can click through to the login page with their email pre-filled.

---

### Edge Cases

- What happens when the external IdP does not provide required claims (e.g., email is missing)? → Display an error explaining which information is missing and suggest using manual registration.
- What happens when the IdP returns a claim value that fails validation (e.g., invalid email format)? → Reject the registration with a user-friendly error and suggest manual registration.
- What happens when the user cancels or fails external IdP authentication? → Return to the registration page with an informational message.
- What happens when multiple IdPs are enabled for registration? → Display all enabled IdPs in their configured sort order.
- What happens when tenant creation checkbox is selected alongside IdP registration? → The option to create a new tenant should be available after successful IdP authentication, using the authenticated identity.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST display external IdP registration options on the registration page when at least one IdP is enabled for registration in the current tenant.
- **FR-002**: System MUST add a new configuration option to IdentityProvider entity: "AllowRegistration" (boolean, default false).
- **FR-003**: System MUST only show IdPs on the registration page that have both "Enabled" = true AND "AllowRegistration" = true.
- **FR-004**: System MUST create a new user account when a user successfully authenticates via an external IdP for registration, using claims provided by the IdP.
- **FR-005**: System MUST map external IdP claims to user attributes (email → Email, given_name → FirstName, family_name → LastName) using existing claim mapping configuration.
- **FR-006**: System MUST prevent duplicate registration when the IdP-provided email already exists in the system.
- **FR-007**: System MUST handle the "Create new tenant" option for users registering via external IdP, allowing tenant creation after successful IdP authentication.
- **FR-008**: System MUST display IdP buttons in the configured sort order (using existing SortOrder property on IdentityProvider).
- **FR-009**: System MUST provide admin UI to configure the "AllowRegistration" setting on the IdP edit page.
- **FR-010**: System MUST gracefully degrade to showing only the manual registration form when no IdPs are enabled for registration.
- **FR-011**: System MUST redirect users to an appropriate success page or auto-approve/login based on existing auto-approval settings after IdP registration.
- **FR-012**: System MUST validate that required claims (at minimum: email) are present before creating an account; if missing, display an appropriate error.

### Key Entities

- **IdentityProvider**: Extended with new `AllowRegistration` boolean property indicating whether this provider can be used for new user registration (in addition to login).
- **Registration**: Existing entity that tracks registration requests; extended to handle registrations originating from external IdP flows with `IsExternalIdp` flag.
- **User**: The user account created from external IdP registration; linked to external identity via existing external login tracking.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can complete registration via external IdP in under 30 seconds (excluding time spent at external provider).
- **SC-002**: Administrators can enable/disable IdP registration for any provider in under 1 minute.
- **SC-003**: 95% of external IdP registrations successfully create user accounts on first attempt when the IdP provides valid claims.
- **SC-004**: Registration page loads and displays IdP options within 2 seconds.
- **SC-005**: Duplicate email registration attempts are detected and blocked 100% of the time with a clear user message.
- **SC-006**: Existing manual registration flow continues to work identically when no IdPs are enabled for registration.

## Assumptions

- External IdPs provide at least an email claim; name claims are optional but recommended.
- The public registration page loads registration-enabled IdPs from the default registration tenant, while the enrollment target can be a new tenant, an invitation tenant, a verified auto-join domain tenant, or a tenant selected by client policy.
- Existing claim mapping configuration on IdPs will be used to map external claims to user attributes.
- Auto-approval settings on the tenant/system level apply to IdP-originated registrations.
- The existing RegistrationService supports the `isExternalIdp` flag and will be reused.

