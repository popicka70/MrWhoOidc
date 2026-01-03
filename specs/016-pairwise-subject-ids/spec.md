# Feature Specification: Pairwise Subject Identifiers

**Feature Branch**: `016-pairwise-subject-ids`  
**Created**: 2026-01-03  
**Status**: Draft  
**Input**: User description: "Implement Pairwise Subject Identifiers. We have a plan in docs/future-plans/pairwise-subject-identifiers.md."

## Clarifications

### Session 2026-01-03

- Q: Should this feature include unit + integration tests as part of Definition of Done? → A: Yes (Option A) — include both unit tests and integration tests.
- Q: Pairwise `sub` generation algorithm? → A: Option B — random opaque `sub` (CSPRNG, base64url, persisted per user+sector).
- Q: Uniqueness scope for `PairwiseSubject`? → A: Option B — unique per tenant.
- Q: What if `sector_identifier_uri` becomes unreachable/invalid at token issuance time? → A: Option A — fail issuance (configuration error), do not fall back.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Issue pairwise `sub` per client (Priority: P1)

As an administrator configuring a relying party (client), I want to enable pairwise subject identifiers so that the same end-user receives a different `sub` value per client (or per sector), preventing cross-application correlation.

**Why this priority**: This is the core privacy outcome and the primary reason to implement pairwise identifiers.

**Independent Test**: Configure one client as pairwise, sign in the same user twice, and verify the `sub` value is pairwise (not the public identifier) and remains stable for that client.

**Acceptance Scenarios**:

1. **Given** a client configured for **pairwise** subject identifiers and an end-user with an existing account, **When** the user completes an authentication flow and receives an ID token (login token) and/or a UserInfo (profile lookup) response, **Then** the `sub` claim is a pairwise identifier value for that user.
2. **Given** the same client configured for **pairwise** subject identifiers and the same end-user, **When** the user authenticates multiple times, **Then** the `sub` claim remains the same across those authentications.
3. **Given** a client configured for **public** subject identifiers, **When** the user authenticates, **Then** the `sub` claim uses the public identifier and does not use a pairwise mapping.

---

### User Story 2 - Control pairwise scope via sector identifier (Priority: P2)

As an administrator, I want to control which clients share the same pairwise `sub` for a user by configuring/deriving a sector identifier, so that the same user can have one shared `sub` across a group of related clients but a different `sub` across unrelated clients.

**Why this priority**: Pairwise identifiers are defined relative to a sector identifier; without this, administrators cannot predict or manage cross-client consistency.

**Independent Test**: Configure two clients as pairwise with the same sector identifier and confirm the user gets the same `sub` across both; configure a third client with a different sector identifier and confirm the `sub` differs.

**Acceptance Scenarios**:

1. **Given** two clients configured for **pairwise** subject identifiers with the same sector identifier, **When** the same end-user authenticates to both clients, **Then** the `sub` claim is identical between those two clients.
2. **Given** two clients configured for **pairwise** subject identifiers with different sector identifiers, **When** the same end-user authenticates to both clients, **Then** the `sub` claim differs between those clients.

---

### User Story 3 - Discover and validate support for subject identifier types (Priority: P3)

As a relying party integrator, I want to learn whether the identity provider supports both public and pairwise subject identifiers so I can correctly integrate, test, and document expected `sub` behavior.

**Why this priority**: This reduces integration confusion and supports standards-based interoperability.

**Independent Test**: Retrieve the provider’s published metadata and verify it advertises support for both subject identifier types.

**Acceptance Scenarios**:

1. **Given** the provider’s published metadata, **When** an integrator reads the supported subject identifier types, **Then** both `public` and `pairwise` are listed.

---

### Edge Cases

- When a client is switched from `public` to `pairwise`, existing end-users will observe a change in `sub` for that client; the system must not silently mix the old public `sub` with the new pairwise `sub`.
- When a client is switched from `pairwise` to `public`, the system must revert to public `sub` behavior for new token/userinfo outputs.
- When a pairwise client has no valid basis to determine a sector identifier (e.g., missing/invalid redirect URIs and no configured sector identifier), the system must fail safely with a clear, actionable error.
- When a configured sector identifier reference is invalid (e.g., not HTTPS or does not match the client’s redirect URIs), the system must reject the configuration and prevent issuance that would produce inconsistent identifiers.
- When a client is configured with a sector identifier reference but the reference cannot be fetched or validated at issuance time, the system must fail issuance safely with a clear error and must not silently fall back to redirect-URI-derived sector values.
- If a pairwise identifier mapping already exists for a user+sector, the system must reuse it rather than generating a new value.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST support both `public` and `pairwise` subject identifier types.
- **FR-002**: System MUST allow administrators to configure each client’s subject identifier type, defaulting to `public` for existing and newly created clients.
- **FR-003**: When a client is configured for `public`, the system MUST return the public subject identifier for that end-user in all applicable outputs.
- **FR-004**: When a client is configured for `pairwise`, the system MUST return a pairwise subject identifier for that end-user in all applicable outputs.
- **FR-005**: For `pairwise` clients, the system MUST ensure the pairwise subject identifier is stable for the same end-user and the same sector identifier over time.
- **FR-006**: For `pairwise` clients, the system MUST ensure pairwise subject identifiers are non-guessable and unique within the issuer.
- **FR-007**: The system MUST persist a mapping of (end-user, sector identifier) → (pairwise subject identifier), and MUST create the mapping on-demand when first needed.
- **FR-008**: The system MUST determine the sector identifier for a pairwise client using either:
  - an administrator-provided sector identifier reference, or
  - a deterministic fallback derived from the client’s configured redirect URIs.
- **FR-009**: If a sector identifier reference is provided, the system MUST validate it according to the provider’s security policy, including at minimum:
  - it uses HTTPS
  - it authoritatively matches the client’s configured redirect URIs
- **FR-010**: The system MUST apply the selected subject identifier type consistently across all relevant outputs where `sub` is defined or returned (e.g., ID token and UserInfo response).
- **FR-011**: The system MUST advertise support for both subject identifier types in its published metadata.
- **FR-012**: The system MUST record an audit event when a new pairwise mapping is created (without logging raw tokens).
- **FR-013**: The implementation MUST include unit tests for sector resolution + pairwise mapping and integration tests verifying `sub` behavior in ID tokens and UserInfo.
- **FR-014**: When creating a new pairwise mapping, the system MUST generate `sub` using a cryptographically secure random byte sequence and encode as base64url (no padding), then persist and reuse it for the same (tenant, user, sector identifier).
- **FR-015**: Pairwise subject identifiers MUST be unique within a tenant (i.e., uniqueness constraints and lookups are tenant-scoped).
- **FR-016**: If `sector_identifier_uri` is configured but cannot be fetched or validated at issuance time, the system MUST fail issuance/UserInfo rather than falling back to redirect-URI-derived sector identifiers.

### Key Entities *(include if feature involves data)*

- **Client**: A relying party configuration including subject identifier type and sector identifier settings.
- **Pairwise Subject Mapping**: A persistent record that links an end-user and a sector identifier to a generated pairwise `sub` value, including creation timestamp.
- **Sector Identifier**: A normalized identifier representing the grouping scope used to determine whether multiple clients share the same pairwise `sub` for the same end-user.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For any client configured as `pairwise`, 100% of issued ID tokens (and UserInfo responses where applicable) contain a `sub` that is stable for the same end-user and sector identifier across at least 30 days of repeated authentications.
- **SC-002**: For the same end-user, two pairwise clients with different sector identifiers produce different `sub` values in 100% of tested cases.
- **SC-003**: For the same end-user, two pairwise clients with the same sector identifier produce identical `sub` values in 100% of tested cases.
- **SC-004**: An administrator can enable or disable pairwise subject identifiers for a client and verify expected `sub` behavior within 5 minutes using only product UI/API documentation.

## Assumptions

- Existing deployments remain compatible by keeping all existing clients set to `public` unless an administrator explicitly opts a client into `pairwise`.
- Pairwise mappings are created on-demand and are not pre-generated for all users.
- Changing a client’s subject identifier type is an administrator-driven breaking change for relying parties and is treated as a deliberate configuration decision.

## Dependencies

- Client management surfaces (UI and/or API) exist and can store per-client subject identifier settings.
- Published provider metadata is available to integrators and can be updated to reflect supported subject identifier types.

