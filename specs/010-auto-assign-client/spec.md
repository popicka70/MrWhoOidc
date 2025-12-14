# Feature Specification: Auto-Assign New Users To Client

**Feature Branch**: `010-auto-assign-client`  
**Created**: 2025-12-14  
**Status**: Draft  
**Input**: User description: "Allow new users who login for the first time to be auto-assigned to the client they were trying to log into. This applies to local username/password sign-up as well as external IdP first-time sign-in. Make it a per-client setting, and update the add/edit client UI to configure it."

## User Scenarios & Testing *(mandatory)*

<!--
  IMPORTANT: User stories should be PRIORITIZED as user journeys ordered by importance.
  Each user story/journey must be INDEPENDENTLY TESTABLE - meaning if you implement just ONE of them,
  you should still have a viable MVP (Minimum Viable Product) that delivers value.
  
  Assign priorities (P1, P2, P3, etc.) to each story, where P1 is the most critical.
  Think of each story as a standalone slice of functionality that can be:
  - Developed independently
  - Tested independently
  - Deployed independently
  - Demonstrated to users independently
-->

### User Story 1 - Auto-assign a brand-new user during login (Priority: P1)

As an end user who is signing in for the first time while trying to access a specific client application, I want my new account to automatically be assigned to that client (when the client allows it), so I can complete sign-in and proceed without admin intervention.

**Why this priority**: Removes the most common “new user blocked” path and reduces manual admin work.

**Independent Test**: Can be fully tested by initiating a login to a client with the setting enabled, creating a brand-new account, and verifying the user ends the journey assigned to that client.

**Acceptance Scenarios**:

1. **Given** a client has “auto-assign new users” enabled and the user does not yet have an account, **When** the user creates a new local username/password account as part of that client’s sign-in journey, **Then** the new user is assigned to that client before completing the sign-in journey.
2. **Given** a client has “auto-assign new users” enabled and the user does not yet have an account, **When** the user signs in via an external identity provider for the first time as part of that client’s sign-in journey, **Then** the new user is assigned to that client before completing the sign-in journey.
3. **Given** a client has “auto-assign new users” disabled, **When** a brand-new user is created through local sign-up or first-time external sign-in during that client’s sign-in journey, **Then** the user is not automatically assigned to the client.

---

### User Story 2 - Configure the behavior per client (Priority: P2)

As an admin who manages client applications, I want to enable or disable “auto-assign new users to this client” per client, so I can balance onboarding convenience with stricter assignment policies for sensitive clients.

**Why this priority**: Makes the behavior explicit, auditable, and safe by default.

**Independent Test**: Can be tested by creating/editing a client and verifying the setting persists and affects the first-time login behavior.

**Acceptance Scenarios**:

1. **Given** an admin is adding a new client, **When** they enable or disable “auto-assign new users to this client”, **Then** the setting is saved for that client and is visible on subsequent edits.
2. **Given** an admin edits an existing client, **When** they change “auto-assign new users to this client”, **Then** the updated setting takes effect for future first-time user creations.

---

### User Story 3 - Safe handling and clear outcomes (Priority: P3)

As a platform operator, I want auto-assignment to happen only when the sign-in is legitimately associated with a real client sign-in attempt, so that a user cannot be assigned to an unintended client through tampering or malformed requests.

**Why this priority**: Prevents accidental or malicious over-assignment.

**Independent Test**: Can be tested by attempting sign-in flows with invalid, missing, or mismatched client identifiers and verifying no unintended assignments occur.

**Acceptance Scenarios**:

1. **Given** a sign-in attempt does not have a valid target client, **When** a new user account is created, **Then** no auto-assignment occurs.
2. **Given** a sign-in attempt targets a valid client but the client has auto-assign disabled, **When** a new user account is created, **Then** no auto-assignment occurs.
3. **Given** a user already exists (not a new user), **When** they sign in to a client that has auto-assign enabled, **Then** the system does not automatically change that user’s client assignments.

---

[Add more user stories as needed, each with an assigned priority]

### Edge Cases

- New user creation succeeds but client assignment fails: user should not be incorrectly marked as assigned; the outcome should be consistent and diagnosable.
- Multiple concurrent first-time sign-in attempts for the same new identity: assignment should not result in duplicate or conflicting assignments.
- Client is deleted/disabled between start of sign-in and completion: auto-assignment should not occur.
- Sign-in is initiated without a client context (e.g., generic “sign in” entry point): no auto-assignment should occur.
- A client belongs to a different tenant than the current sign-in context: auto-assignment must not cross tenant boundaries.

## Requirements *(mandatory)*

<!--
  ACTION REQUIRED: The content in this section represents placeholders.
  Fill them out with the right functional requirements.
-->

### Functional Requirements

- **FR-001**: System MUST provide a per-client configuration setting that enables or disables “auto-assign new users to this client”.
- **FR-002**: System MUST default the per-client “auto-assign new users to this client” setting to disabled for existing and newly created clients unless explicitly enabled.
- **FR-003**: When the per-client setting is enabled, the system MUST automatically assign a newly created user to the client that is the target of the sign-in journey.
- **FR-004**: The auto-assignment behavior MUST apply to both (a) local username/password account creation and (b) first-time sign-in via an external identity provider, when that sign-in results in creation of a new user.
- **FR-005**: The system MUST NOT auto-assign users who already exist (even if they are signing in to the client for the first time).
- **FR-006**: The system MUST perform auto-assignment only when the sign-in journey is associated with a valid, known client and must not accept an arbitrary client identifier from untrusted input.
- **FR-007**: The system MUST NOT create client assignments that violate tenant boundaries.
- **FR-008**: The admin client add/edit experience MUST allow viewing and changing the per-client setting.
- **FR-009**: The system MUST record an audit-relevant event when an auto-assignment occurs, including at minimum the user identity, the client, and the time.

### Key Entities *(include if feature involves data)*

- **Client**: A relying party/application configuration, including a boolean setting “auto-assign new users to this client”.
- **User**: An end-user identity that can be newly created via local sign-up or created on first-time external sign-in.
- **User-Client Assignment**: A record that a given user is permitted/assigned to a given client.
- **Audit Event**: A record of security-relevant actions, including auto-assignment events.

## Assumptions

- “New user” means an account that is created during the current sign-in journey (not a previously existing account).
- The “client the user was trying to log into” is the client that is the target of the current sign-in/authorization journey, as determined by the system’s validated request context.
- If a user signs in outside a client-specific context (no target client), no auto-assignment occurs.

## Success Criteria *(mandatory)*

<!--
  ACTION REQUIRED: Define measurable success criteria.
  These must be technology-agnostic and measurable.
-->

### Measurable Outcomes

- **SC-001**: For clients that enable auto-assign, at least 95% of first-time sign-ins that create a new user complete successfully without requiring any admin assignment step.
- **SC-002**: Enabling or disabling the per-client setting can be completed by an admin in under 60 seconds and is reflected immediately for subsequent first-time sign-ins.
- **SC-003**: Auto-assignment does not increase incorrect cross-tenant assignment incidents (target: 0 verified incidents).
- **SC-004**: Support/admin tickets requesting “please assign this new user to the app” decrease by at least 50% for clients that enable the feature.

