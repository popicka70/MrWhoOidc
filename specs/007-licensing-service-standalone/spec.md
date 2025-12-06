# Feature Specification: Standalone Licensing Service

**Feature Branch**: `007-licensing-service-standalone`  
**Created**: 2024-12-04  
**Status**: Draft  
**Input**: User description: "I want to make the MrWhoOidc.KeyGen application a separate service that will issue license keys not only for the MrWhoOidc.WebAuth app but also for other applications. For that we'll need to store issued licenses in a database. We'll have to support license renewal and similar license actions. I'll make a separate github repo for the service but as of now we'll start by putting it into a separate subfolder including associated tests."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Issue License for Any Application (Priority: P1)

As a licensing administrator, I want to issue license tokens for any registered application (not just MrWhoOidc.WebAuth), so that I can manage licensing across my entire product portfolio from a single service.

**Why this priority**: This is the core value proposition - transforming a single-purpose tool into a multi-application licensing platform. Without this capability, the service cannot fulfill its primary purpose.

**Independent Test**: Can be fully tested by registering a new application in the system, generating a license for that application, and verifying the license contains the correct application identifier and claims. Delivers immediate value by enabling licensing for multiple products.

**Acceptance Scenarios**:

1. **Given** an administrator is logged in and an application "ProductX" is registered, **When** they create a license for "ProductX" with tier "Professional" and 1-year validity, **Then** a signed license token is generated containing the application identifier "ProductX" and all specified claims.

2. **Given** an administrator is creating a license, **When** they select an application from the registered applications list, **Then** the license generation form displays application-specific options (features, limits) defined for that application.

3. **Given** a license has been issued for "ProductX", **When** the administrator views issued licenses, **Then** they can filter by application to see only "ProductX" licenses.

---

### User Story 2 - Register and Configure Applications (Priority: P1)

As a licensing administrator, I want to register applications that can receive licenses, specifying their unique identifiers, available feature catalogs, and license tier definitions, so that licenses are correctly scoped to each product.

**Why this priority**: Applications must be registered before licenses can be issued. This is a foundational capability that enables the multi-application licensing model.

**Independent Test**: Can be tested by registering a new application with its feature catalog and tier definitions, then verifying the application appears in the system and its configuration is correctly stored.

**Acceptance Scenarios**:

1. **Given** an administrator is on the application management page, **When** they create a new application with identifier "MyApp", display name "My Application", and a feature catalog containing ["feature-a", "feature-b"], **Then** the application is registered and available for license issuance.

2. **Given** an application "MyApp" exists, **When** the administrator edits its configuration to add a new feature "feature-c", **Then** the feature becomes available in license generation for that application.

3. **Given** an application "MyApp" exists with issued licenses, **When** the administrator attempts to delete it, **Then** the system prevents deletion and displays a warning about existing licenses.

---

### User Story 3 - Store and Track Issued Licenses (Priority: P1)

As a licensing administrator, I want all issued licenses to be stored in a database with full metadata, so that I can track, audit, and manage the entire license lifecycle.

**Why this priority**: Persistent storage is essential for license management, renewal, and audit capabilities. This transforms ephemeral license generation into a managed licensing system.

**Independent Test**: Can be tested by issuing a license, then querying the database to verify the license metadata is stored, including token ID, application, tier, validity period, and generation timestamp.

**Acceptance Scenarios**:

1. **Given** an administrator generates a license, **When** the license is created, **Then** the license metadata is persisted to the database including: token ID (jti), application identifier, tier, scope, validity dates, features, limits, and generation timestamp.

2. **Given** licenses have been issued, **When** an administrator views the license list, **Then** they see all licenses with status indicators (active, expired, revoked, pending renewal).

3. **Given** a license exists in the database, **When** the administrator views its details, **Then** they see the complete audit trail including who created it, when, and any subsequent actions.

---

### User Story 4 - Renew Existing Licenses (Priority: P2)

As a licensing administrator, I want to renew an existing license by extending its validity period while preserving its configuration, so that customers can continue using the product without reconfiguration.

**Why this priority**: License renewal is a common operational need that directly supports customer retention. It builds on the storage capability from P1.

**Independent Test**: Can be tested by issuing a license, then performing a renewal action that extends the validity period, and verifying a new license token is generated with extended dates while the original license is marked as superseded.

**Acceptance Scenarios**:

1. **Given** an active license exists for "ProductX" expiring on 2025-01-01, **When** the administrator renews it for 1 year, **Then** a new license token is generated with validity starting immediately (60-day overlap with existing license) through 2026-01-01, and the original license remains active until its original expiry.

2. **Given** an administrator is renewing a license, **When** they complete the renewal, **Then** the new license inherits all configuration from the original (tier, features, limits) unless explicitly modified, and both licenses are valid during the 60-day overlap period.

3. **Given** a license has been renewed, **When** viewing the license history, **Then** the relationship between original and renewed licenses is clearly shown.

---

### User Story 5 - Revoke Licenses (Priority: P2)

As a licensing administrator, I want to revoke a license before its expiration, so that I can respond to contract terminations, security incidents, or policy violations.

**Why this priority**: Revocation is essential for security and business compliance. It provides control over the license lifecycle.

**Independent Test**: Can be tested by issuing a license, performing a revocation action with a reason, and verifying the license status changes to "revoked" with the timestamp and reason recorded.

**Acceptance Scenarios**:

1. **Given** an active license exists, **When** the administrator revokes it with reason "Contract terminated", **Then** the license status changes to "revoked" and the reason and timestamp are recorded.

2. **Given** a license has been revoked, **When** viewed in the license list, **Then** it displays as "revoked" with the revocation date and reason visible.

3. **Given** a license has been revoked, **When** the administrator attempts to renew it, **Then** the system prevents renewal and suggests creating a new license instead.

---

### User Story 6 - License Validation Endpoint (Priority: P2)

As a consuming application, I want to validate a license token against the licensing service, so that I can verify the license is authentic, not revoked, and still valid.

**Why this priority**: Remote validation enables revocation enforcement and provides applications with authoritative license status. This is critical for security.

**Independent Test**: Can be tested by calling the validation endpoint with a valid license token and verifying the response confirms validity; then revoking the license and verifying the endpoint returns invalid.

**Acceptance Scenarios**:

1. **Given** a valid, active license token, **When** an application calls the validation endpoint with the token, **Then** the service returns a success response with license metadata (application, tier, features, expiry).

2. **Given** a revoked license token, **When** an application calls the validation endpoint, **Then** the service returns an invalid response indicating the license has been revoked.

3. **Given** an expired license token, **When** an application calls the validation endpoint, **Then** the service returns an invalid response indicating the license has expired.

4. **Given** an unknown or tampered token, **When** an application calls the validation endpoint, **Then** the service returns an invalid response indicating signature verification failed.

---

### User Story 7 - Upgrade/Downgrade License Tier (Priority: P3)

As a licensing administrator, I want to change a license's tier (e.g., from Professional to Enterprise), so that I can accommodate customer subscription changes.

**Why this priority**: Tier changes are less frequent than renewals but still important for business flexibility. Builds on core license management.

**Independent Test**: Can be tested by issuing a Professional license, performing a tier upgrade to Enterprise, and verifying a new license is generated with Enterprise tier and features while the original is marked as superseded.

**Acceptance Scenarios**:

1. **Given** a Professional license exists for a customer, **When** the administrator upgrades it to Enterprise, **Then** a new license is generated with Enterprise tier, expanded features, and the original is marked as "upgraded".

2. **Given** an Enterprise license exists, **When** the administrator downgrades it to Professional, **Then** a new license is generated with Professional tier, and features not included in Professional are removed.

---

### User Story 8 - Bulk License Operations (Priority: P3)

As a licensing administrator, I want to perform bulk operations (renew, revoke) on multiple licenses at once, so that I can efficiently manage large customer bases.

**Why this priority**: Operational efficiency improvement for scale. Not required for initial functionality but valuable for production use.

**Independent Test**: Can be tested by selecting multiple licenses and performing a bulk renewal, verifying all selected licenses are renewed with new tokens generated.

**Acceptance Scenarios**:

1. **Given** 10 licenses are expiring this month, **When** the administrator selects them and performs bulk renewal for 1 year, **Then** all 10 licenses are renewed and new tokens are generated.

2. **Given** the administrator selects 5 licenses from different applications, **When** they attempt bulk revocation, **Then** all 5 are revoked with the same timestamp and reason.

---

### Edge Cases

- What happens when a renewal is attempted on an already-expired license? System allows renewal but sets validity from current date, not original expiry.
- How does the system handle concurrent license operations on the same license? Optimistic concurrency with clear error messaging if conflict detected.
- What happens if the signing key is rotated while licenses exist? Existing licenses remain valid; new licenses use new key; JWKS endpoint serves all active keys.
- What if an application is soft-deleted but has active licenses? Licenses remain valid; application cannot be hard-deleted; reactivation is possible.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST allow registration of licensed products with unique identifiers, display names, and metadata.
- **FR-001a**: System MUST allow management of customers as first-class entities with CRUD operations (create, read, update, soft-delete).
- **FR-001b**: System MUST associate each license with exactly one customer and one licensed product.
- **FR-002**: System MUST support product-specific licensable options catalogs, where each product defines its available options with name, data type (string, number, boolean), and optional default value.
- **FR-002a**: System MUST store license options as key-value pairs, where option names are defined by the product catalog and values can be strings or numbers.
- **FR-003**: System MUST support application-specific license tier definitions (e.g., Community, Professional, Enterprise).
- **FR-004**: System MUST generate signed license tokens (JWTs) containing application identifier, tier, features, limits, and validity period.
- **FR-005**: System MUST persist all issued license metadata to a database for lifecycle management.
- **FR-006**: System MUST track license status: active, expired, revoked, renewed, upgraded, downgraded.
- **FR-007**: System MUST support license renewal, extending validity while optionally modifying configuration.
- **FR-007a**: System MUST issue renewal licenses with a 60-day overlap period where both old and new licenses are valid, providing customer transition grace period.
- **FR-008**: System MUST support license revocation with mandatory reason and timestamp recording.
- **FR-009**: System MUST provide a validation endpoint for consuming applications to verify license authenticity and status, protected by OIDC authentication.
- **FR-009a**: System MUST require OIDC authentication for all license management operations (create, renew, revoke, validate).
- **FR-010**: System MUST support license tier upgrades and downgrades with configuration inheritance.
- **FR-011**: System MUST maintain audit trail for all license lifecycle events (creation, renewal, revocation, tier change).
- **FR-012**: System MUST support bulk operations (renewal, revocation) on multiple licenses.
- **FR-013**: System MUST prevent deletion of applications that have active or historical licenses.
- **FR-014**: System MUST provide filtering and search capabilities for licenses with customer as the primary search dimension, then product, status, and expiry date.
- **FR-014a**: System MUST support customer-first search workflow: select/search customer → view all their licenses → optionally filter by product to locate license for renewal.
- **FR-015**: System MUST expose JWKS endpoint serving public keys for license signature verification.
- **FR-016**: System MUST support signing key rotation without invalidating existing licenses.
- **FR-017**: System MUST be deployable as a standalone service independent of MrWhoOidc.WebAuth.

### Key Entities

- **Customer**: Represents a licensed customer/organization. Contains unique identifier, display name, contact information, and status (active/inactive). Customers can hold multiple licenses across different products.

- **Licensed Product**: Represents a licensable product/service (formerly "Application"). Contains unique identifier, display name, product-specific licensable options catalog, tier definitions, and status (active/inactive).

- **Product Option Definition**: Represents a licensable option available for a product. Contains option key (unique within product), display name, data type (string/number/boolean), optional default value, and description. Each licensed product has its own set of option definitions.

- **License**: Represents an issued license token. Contains token ID (jti), associated customer, licensed product, tier, scope, validity period (not-before, expiry), selected product options as key-value pairs (e.g., {"max_users": 100, "region": "EU", "analytics": true}), status, and relationships to parent/child licenses (for renewals/upgrades).

- **LicenseEvent**: Represents an audit trail entry. Contains license reference, event type (created, renewed, revoked, upgraded, downgraded), timestamp, actor, and details/reason.

- **SigningKey**: Represents a signing key for license tokens. Contains key ID (kid), key material reference, algorithm, status (active/rotated/retired), and validity period.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Administrators can register a new application and issue its first license within 5 minutes.
- **SC-002**: License renewal can be completed in under 30 seconds from the license details page.
- **SC-003**: The validation endpoint responds to license verification requests in under 200 milliseconds for 99% of requests.
- **SC-004**: System correctly tracks 100% of license lifecycle events in the audit trail.
- **SC-005**: Bulk operations can process 100 licenses in under 10 seconds.
- **SC-006**: The service can be deployed and operational without any MrWhoOidc.WebAuth dependencies.
- **SC-007**: All existing MrWhoOidc.KeyGen functionality (key generation, current license generation) continues to work without regression.

## Assumptions

- The service will use the existing signing key infrastructure from MrWhoOidc.KeyGen.
- Initial deployment will use SQLite for development and PostgreSQL for production, consistent with existing patterns.
- All license operations (validation, renewal, management) require OIDC authentication; only authenticated users can access the service.
- License tokens remain self-verifiable via embedded signature for offline validation; the online validation endpoint provides additional revocation status checking.
- The separate subfolder structure will mirror the eventual standalone repository structure to ease future extraction.
- Existing MrWhoOidc.KeyGen tests will be preserved and extended, not replaced.

## Clarifications

### Session 2025-12-04

- Q: What is the default license overlap duration for renewals? → A: 60 days overlap
- Q: How should customers be managed in the system? → A: Customer is a first-class entity with its own CRUD management
- Q: How should product-specific licensable options be structured? → A: Key-value pairs (option name → value, supporting strings/numbers)
- Q: What is the primary license search pattern? → A: Search by customer first, then filter by product
- Q: How should the validation endpoint be authenticated? → A: OIDC authentication required for all license operations

## Out of Scope

- Customer self-service portal (license management remains administrator-only).
- Automated license provisioning via integrations (e.g., Stripe webhooks).
- License usage analytics and reporting dashboards.
- Multi-tenant administration (single administrative context for now).
- Offline license validation SDKs for consuming applications.
