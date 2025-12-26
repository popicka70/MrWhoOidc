# Feature Specification: Platform QR Login at DiscoverTenant

**Feature Branch**: `014-platform-qr-login`  
**Created**: 2025-12-26  
**Status**: Draft  
**Input**: User description: "Allow QR login at the /DiscoverTenant page as an optional system-wide setting configurable via a new platform settings page"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Platform Administrator Enables QR Login (Priority: P1)

A platform administrator wants to enable QR code login as an authentication option at the central login discovery page (/DiscoverTenant), allowing users across all tenants to initiate QR-based authentication before selecting their organization.

**Why this priority**: This is the foundational capability. Without enabling the feature at the platform level, QR login cannot appear on the DiscoverTenant page. All other user stories depend on this being available.

**Independent Test**: Can be fully tested by accessing a new Platform Settings page, toggling the QR login setting, and verifying the change persists. Delivers immediate value by providing platform-wide configuration capability.

**Acceptance Scenarios**:

1. **Given** a platform administrator is logged in and has access to admin functions, **When** they navigate to the Platform Settings page, **Then** they see a toggle for "Enable QR Login at Discovery Page" with the current state displayed
2. **Given** a platform administrator is on the Platform Settings page, **When** they enable the QR login option and save, **Then** the setting is persisted and a success message is displayed
3. **Given** the QR login platform setting is disabled (default state), **When** anyone visits the DiscoverTenant page, **Then** no QR login option is visible

---

### User Story 2 - User Initiates QR Login at DiscoverTenant (Priority: P2)

A user who prefers QR-based authentication can initiate a QR login session directly from the DiscoverTenant page without first entering their email or selecting a tenant.

**Why this priority**: This delivers the primary user-facing value of the feature. Once platform administrators can enable QR login (P1), users should be able to use it.

**Independent Test**: Can be tested by visiting the DiscoverTenant page when QR login is enabled, clicking the QR login option, scanning the displayed QR code with a mobile device, and completing authentication.

**Acceptance Scenarios**:

1. **Given** platform QR login is enabled, **When** a user visits the DiscoverTenant page, **Then** they see a "Sign in with QR Code" button/option alongside the email input and external IdP list
2. **Given** a user clicks the QR login option, **When** the QR code is displayed, **Then** the QR code is scannable and contains a valid session token
3. **Given** a user has initiated QR login, **When** they scan the QR code on their authenticated mobile device and approve, **Then** the desktop browser session is authenticated and the user is redirected appropriately
4. **Given** a user is viewing the QR code, **When** the session expires without approval, **Then** the user sees an expiration message and can restart the QR flow

---

### User Story 3 - Platform Settings Page for System-Wide Configuration (Priority: P3)

A platform administrator needs a dedicated page to manage system-wide (platform-level) settings that apply across all tenants, separate from tenant-specific settings.

**Why this priority**: While the QR login toggle (P1) could theoretically be added elsewhere, having a proper Platform Settings page establishes the pattern for future platform-wide configurations and provides a clear administrative boundary.

**Independent Test**: Can be tested by navigating to Platform Settings, viewing available system-wide options, making changes, and verifying they apply globally rather than to a single tenant.

**Acceptance Scenarios**:

1. **Given** a platform administrator navigates to the admin area, **When** they look for platform settings, **Then** they find a "Platform Settings" link/menu item distinct from tenant settings
2. **Given** a platform administrator is on the Platform Settings page, **When** they view the page, **Then** settings are clearly labeled as platform-wide and distinct from tenant-specific overrides
3. **Given** platform settings exist, **When** multiple tenants are active, **Then** platform settings apply to the discovery page regardless of which tenant a user eventually logs into

---

### Edge Cases

- What happens when a user starts QR login at DiscoverTenant but their mobile device is logged into a different identity provider instance?
  - The QR session contains sufficient context to route to this instance; authentication still succeeds if the mobile device can reach this OP
- What happens when QR login is disabled mid-session while a user has a QR code displayed?
  - The session should gracefully expire or fail with a user-friendly message; existing scanned sessions in progress should complete
- How does the system handle concurrent QR login sessions from the same browser?
  - Only one active QR session per browser; starting a new QR session cancels any previous pending session
- What happens when returnUrl is provided to DiscoverTenant and user chooses QR login?
  - The returnUrl is preserved through the QR flow and used after successful authentication

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide a Platform Settings administrative page accessible to platform administrators
- **FR-002**: System MUST include an "Enable QR Login at Discovery Page" toggle on the Platform Settings page
- **FR-003**: System MUST persist platform settings independently of tenant settings (platform-level storage)
- **FR-004**: System MUST display a QR login option on the DiscoverTenant page when the platform setting is enabled
- **FR-005**: System MUST hide the QR login option on the DiscoverTenant page when the platform setting is disabled (default)
- **FR-006**: System MUST generate valid QR codes for platform-level QR login that work with the existing QR authentication flow
- **FR-007**: System MUST preserve any returnUrl parameter through the QR login flow initiated from DiscoverTenant
- **FR-008**: System MUST handle QR session expiration gracefully with clear user feedback
- **FR-009**: Platform settings page MUST be protected by appropriate authorization (platform admin role)
- **FR-010**: System MUST provide appropriate visual hierarchy on DiscoverTenant showing QR login as an alternative to email/IdP login

### Key Entities *(include if feature involves data)*

- **PlatformSettings**: System-wide configuration that applies across all tenants. Key attributes: QrLoginAtDiscoveryEnabled (bool), other future platform-wide toggles. Stored separately from tenant settings.
- **QrSession (existing)**: Represents an active QR login session. For platform-level QR login, the session is not tenant-bound initially and resolves to a tenant upon mobile authentication.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Platform administrators can enable/disable QR login at discovery page within 3 clicks from the admin dashboard
- **SC-002**: When enabled, the QR login option is visible to 100% of users visiting the DiscoverTenant page
- **SC-003**: QR login flow from DiscoverTenant completes with the same success rate as QR login from client-initiated flows
- **SC-004**: Platform settings changes take effect immediately (within 5 seconds) without requiring application restart
- **SC-005**: Zero regression in existing QR login functionality for client-associated flows
- **SC-006**: New Platform Settings page loads in under 2 seconds under normal conditions

## Assumptions

- The existing QR login infrastructure (QrLoginHandler, QrLoginService, QR code generation) can be reused for platform-level QR login with minimal modification
- Platform administrators have a distinct role or permission set that can be leveraged for access control to Platform Settings
- The database schema can accommodate a platform-level settings entity or configuration
- Mobile authentication flow already handles routing to the correct OP instance
- Default behavior is QR login disabled at discovery page (opt-in feature)

