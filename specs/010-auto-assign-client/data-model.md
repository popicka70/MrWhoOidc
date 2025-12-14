# Phase 1 Data Model: Auto-Assign New Users To Client

## Entities

### Client

- **Purpose**: Stores relying party (client application) configuration and policy knobs.
- **New field**:
  - `AutoAssignNewUsersToClient` (boolean; default: false)
- **Notes/constraints**:
  - Must remain tenant-scoped.
  - Assignment decisions must not cross tenants.

### User

- **Purpose**: Represents a user identity within a tenant.
- **Lifecycle**:
  - Created via local registration approval or external auto-provisioning/auto-approval.

### Registration

- **Purpose**: Tracks pending and approved registrations.
- **Relevant fields**:
  - `ClientId` (optional association to a client for post-creation assignment)
  - `TenantId` (required)
  - `State` (pending/approved/rejected)

### UserClientAssignment

- **Purpose**: Grants/records a user’s assignment to a client.
- **Relevant fields/constraints**:
  - Must link `UserId` + `ClientId` + `RealmId`.
  - Must not create duplicates.
  - Must remain consistent with tenant and realm boundaries.

## Invariants & Validation Rules

- Assignment is only created when:
  - A brand-new user is created during the current onboarding flow, AND
  - The target client is known and valid for the current flow, AND
  - The per-client `AutoAssignNewUsersToClient` setting is enabled.
- For existing users, the feature must not modify client assignments.
- Assignment must be tenant-safe:
  - The user’s tenant and the client’s tenant must match.
- Assignment must be realm-safe:
  - The assignment must use the client’s realm.

## State Transitions

- **Registration**: `pending` → `approved` (creates User; may create assignment if `ClientId` is set and allowed)
- **External provisioning**:
  - “auto_approved” path: creates Registration + User; may create assignment.
  - “auto_provisioned” path: creates User directly; feature will add assignment when enabled.
