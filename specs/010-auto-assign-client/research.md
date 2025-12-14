# Phase 0 Research: Auto-Assign New Users To Client

## Decision 1: Add a dedicated per-client flag

**Decision**: Introduce a per-client boolean setting (e.g., “Auto-assign new users to this client”), defaulting to disabled.

**Rationale**:
- Matches the feature requirement (“It should be client parameter”).
- Keeps behavior explicit and opt-in (safer default for sensitive clients).
- Fits the existing pattern of per-client policy knobs (login methods, external provisioning policies, auto-approval mode).

**Alternatives considered**:
- Reuse existing settings (e.g., AutoApprovalMode): rejected because approval and assignment are distinct concerns.
- Global configuration flag: rejected because requirement is per client.

## Decision 2: Use validated client context, not untrusted inputs

**Decision**: Determine the target client from the system’s validated authorization context.

**Rationale**:
- The `ReturnUrl` may not always contain `client_id` (e.g., PAR sanitization can leave only `request_uri`).
- Security: prevents auto-assigning users based on tampered query strings.

**Alternatives considered**:
- Parse `client_id` out of `ReturnUrl`: acceptable only as a fallback because it can be absent or incomplete.

## Decision 3: External IdP provisioning should key off the explicit client id

**Decision**: In external IdP provisioning, use the explicit client id already carried through the external OIDC flow (the `clientId` value from the external state) to load client policy and drive auto-assignment.

**Rationale**:
- External start is invoked by `/authorize` after validating the effective request; the passed client id is already the “right” one.
- Avoids reliance on `ReturnUrl` parsing.

**Alternatives considered**:
- Always force the external registration auto-approve path: rejected because some clients may allow external auto-provisioning without auto-approval.

## Decision 4: Local registration must preserve ReturnUrl and validate it

**Decision**: Preserve `ReturnUrl` when navigating from the login page to registration, and (when present) validate it to determine the target client and decide whether to pass a client association into registration.

**Rationale**:
- This is the only reliable way to know “the client the user was trying to log into” for local sign-up.
- Validation must support query, JAR, and PAR flows.

**Alternatives considered**:
- A generic registration flow that never binds to a client: rejected because it cannot meet the feature requirement.

## Decision 5: Use existing assignment model

**Decision**: Create assignments using the existing User-Client assignment mechanism (including realm/tenant constraints).

**Rationale**:
- Assignment enforcement already exists; this feature fills the missing “first-time onboarding” linkage.
- Minimizes new schema beyond the per-client flag.

## Open items for implementation (not spec gaps)

- Decide the minimum audit trail: structured log event vs. durable audit entity.
- Ensure assignment is only applied when the user was created during the current flow (not for existing users).
