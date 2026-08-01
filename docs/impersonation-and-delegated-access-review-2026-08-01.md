# Tenant Support Access and User Delegation Review

**Reviewed:** 2026-08-01  
**Branch:** `sol/impersonation`  
**Scope:** Platform administration support access and temporary, client-bound user-to-user delegation

**Implementation update:** The first vertical slice from this review is now implemented: delegated-token introspection revalidates durable grant state, the Test API enforces a real `profile.read` resource, the RazorClient has a one-click grant handoff, support-session revocation is exposed in the history UI, and focused unit/E2E syntax checks pass.

## Executive conclusion

MrWhoOidc has two correctly separated foundations:

1. **Tenant Support Access** lets a platform administrator temporarily inspect one tenant as a read-only tenant administrator. This is substantially implemented and should not be called user impersonation because no user identity is assumed.
2. **Delegated Access Grants** let one tenant member authorize another tenant member to act on their behalf for one OIDC client, selected capabilities, selected resources, and a bounded time. The first end-to-end `profile.read` resource path is now implemented; further production resource integrations remain.

The principal remaining release work is production adoption and operational proof. The demo now consumes delegated authority through a real profile resource operation and introspects the token on each request. Other resource servers still need to adopt the same active-grant and object-level enforcement contract.

| Direction | Current state | Assessment |
|---|---|---|
| Platform administrator helping manage the IdP | Durable, bounded, read-only support sessions with audit and UI | Close to releasable after hardening and operational cleanup |
| User delegating temporary rights for a client | Durable client-bound grants, invitation/acceptance UI, session activation, RFC 8693 exchange, and protected demo resource | First vertical slice complete; production resource integrations remain |

## Findings

### High: production resource servers still need the demo authorization contract

[`IDelegatedAccessAuthorizationService`](../MrWhoOidc.Auth/Services/Delegation/IDelegatedAccessAuthorizationService.cs) performs the important checks: actor, bound client, grant status and lifetime, both memberships, tenant status, capability, and resource ID. Its production caller is the token-exchange service. No client-owned API or WebAuth business operation calls it to authorize the requested resource operation.

Browser activation sets `DelegatedAccessGrantId` in the session. [`EffectiveAccessContextAccessor`](../MrWhoOidc.WebAuth/Services/EffectiveAccessContextAccessor.cs) then resolves actor and subject, but it does not authorize a capability or resource. The only account page using the accessor, [`Profile.cshtml.cs`](../MrWhoOidc.WebAuth/Pages/Account/Profile.cshtml.cs), returns no profile for delegated GET requests and forbids delegated POST requests.

**Resolution:** the example Test API now exposes `GET /profiles/{profileId}/summary`, requires `profile`, introspects the token, validates the actor/subject/client/grant, and checks the requested user ID against `profile.read` resources. Production APIs must adopt this contract before additional capabilities are enabled.

### Resolved for the demo: resource server now enforces object-level authority

[`Examples/MrWhoOidc.TestApi/Program.cs`](../Examples/MrWhoOidc.TestApi/Program.cs) retains `/me` for diagnostics, but the delegated demo calls `/profiles/{profileId}/summary`. The API uses the configured `test-api` client to introspect the exchanged token, validates `delegation_id`, `act.sub`, the bound client, current grant state, and the requested resource ID.

**Remaining impact:** `/me` is intentionally a claims diagnostic and must not be treated as an authorization example. Any new downstream API needs object-level checks like the profile endpoint.

### High: revocation does not invalidate an already-issued self-contained token

Revocation immediately prevents browser context reuse and new delegated exchanges because the durable grant is reloaded. The demo resource API also introspects every request, so revocation is effective on the next profile request. A generic resource server that only validates a self-contained JWT still accepts it until access-token expiry.

**Remaining impact:** “immediate revocation” is now true for the demo API, but other APIs must use introspection/reference tokens or document a bounded JWT acceptance window.

### Medium: browser context still needs operation-level capability checks

Activation and subsequent context resolution now validate feature state, actor, client binding, grant status, time window, tenant status, and both memberships. The context remains a routing/display context; individual browser operations must still call the capability/resource authorization service rather than treating session activation alone as permission.

**Remaining impact:** no new write capability should be added until each browser operation has its own capability/resource authorization call.

### Medium: delegated token subject identifiers bypass normal subject policy

[`TokenExchangeService`](../MrWhoOidc.Auth/Services/TokenExchangeService.cs) emits the delegator's internal `UserAccount` GUID as `sub` and the delegate's internal GUID as `act.sub`. The current code does not apply the normal public/pairwise subject calculation for the target client and sector.

**Impact:** internal identifiers can become correlatable across clients, and pairwise-subject clients receive inconsistent subject semantics.

### Resolved: support-session revocation is exposed to platform administrators

The support history UI now lists an action for active sessions, requires a reason, audits the revocation, and clears the current browser reference when applicable. Starting another support session is rejected while the same platform administrator has an unexpired active session.

**Remaining impact:** incident-response E2E coverage should exercise revoking another administrator's session, not only the current administrator's session.

### Medium: authorization and audit coverage is incomplete

Support access fails closed by HTTP method for unannotated tenant-admin requests and explicit Minimal API operation metadata is present in the main route groups. However, there is no automated endpoint-inventory test proving that every tenant-admin endpoint is classified, and unit tests cover classification helpers and the store rather than the full authorization handler.

Delegated authorization now persists `LastUsedAt` and increments `UseCount`. The lifecycle payloads still include raw account IDs alongside hashed actor/subject values, delegated events do not consistently include correlation identifiers, and there are no dedicated delegated-access metrics or alerts.

### Resolved: support metrics distinguish use from stop

Successful support authorization now increments `TenantSupportAccessUses`; `TenantSupportAccessStops` is reserved for explicit session termination.

### Low: active documentation still reports the plan as proposed

[`tenant-support-and-delegated-access-implementation-plan.md`](tenant-support-and-delegated-access-implementation-plan.md) describes the intended model well, but many acceptance criteria remain unchecked and should be reconciled with the verified implementation status. Historical “impersonation complete” documents also describe the superseded session-only design.

## How Tenant Support Access works now

```mermaid
sequenceDiagram
    actor Admin as Platform administrator
    participant UI as Support Access UI
    participant Service as TenantSupportAccessService
    participant DB as PostgreSQL
    participant Authz as TenantAdminAuthorizationHandler

    Admin->>UI: Select tenant, reason, ticket, duration
    UI->>Service: Start support access
    Service->>Service: Re-authorize platform-admin role
    Service->>DB: Create active read-only session
    Service-->>Admin: Store session ID in server session
    Admin->>Authz: Request tenant-admin page or API
    Authz->>DB: Reload support session and actor role
    Authz->>DB: Verify tenant active and session unexpired
    alt Read operation
        Authz-->>Admin: Allow and audit use
    else Write operation
        Authz-->>Admin: Deny and audit write attempt
    end
```

1. A platform administrator opens `/platform-admin/support-access` and selects an active tenant.
2. A reason is required; ticket reference and duration are optional. Duration defaults to 15 minutes and is bounded to 1-60 minutes.
3. [`TenantSupportAccessService`](../MrWhoOidc.WebAuth/Services/TenantSupportAccessService.cs) verifies the platform-admin policy, creates a durable `TenantSupportAccessSession`, audits the start, and stores only its ID in ASP.NET session.
4. The tenant-admin policy reloads the durable record on each request. It verifies actor ownership, current platform-admin role, active target tenant, active status, and absolute expiry.
5. Read-only mode allows safe requests. Unsafe Razor Page methods are blocked by [`SupportAccessReadOnlyPageFilter`](../MrWhoOidc.WebAuth/Security/Admin/SupportAccessReadOnlyPageFilter.cs); the authorization handler denies write operation requirements and treats unannotated unsafe methods as writes.
6. The UI shows a persistent support banner. Explicit stop marks the record ended, audits duration, and clears the session reference. Local logout also ends the current support session.
7. A cleanup worker finalizes expired active records. The store supports revocation, although no current page exposes it.

The authenticated principal always remains the platform administrator. The effective context has the administrator as actor, no user subject, the selected tenant, and the durable support-session ID.

## How Delegated Access works now

```mermaid
sequenceDiagram
    actor Owner as Delegator
    actor Helper as Delegate
    participant IdP as MrWhoOidc
    participant Client as Bound client
    participant API as Resource API

    Owner->>IdP: Create client-bound capability grant
    IdP-->>Helper: Single-use invitation
    Helper->>IdP: Review and accept
    Helper->>Client: Sign in as self
    Client->>IdP: Exchange helper token + delegation_id
    IdP->>IdP: Validate grant, client, users, capability, resource
    IdP-->>Client: Token: sub=owner, act.sub=helper
    Client->>API: Call with delegated token
    API-->>Client: Current implementation only echoes claims
```

1. The delegator and delegate must be different users with active memberships in the same tenant.
2. The delegator selects an OIDC client, an eligible delegate, a purpose, expiry, and capabilities.
3. The initial catalog allows only `profile.read`. The service binds that capability to the delegator's user resource and enforces the strictest configured/capability lifetime.
4. The grant starts in `PendingAcceptance`. A random invitation token is stored only as a SHA-256 hash and emailed to the delegate.
5. Only the invited account can accept or decline. Acceptance atomically consumes the token and activates the grant.
6. The delegator can revoke; the delegate can relinquish. The delegate can separately activate or exit a browser display context without changing grant state.
7. The bound confidential client may perform RFC 8693 token exchange using the delegate's subject token and private `delegation_id` parameter.
8. Exchange validates the client-bound grant through the central authorization service, intersects requested, subject-token, grant-mapped, and client-policy scopes, and bounds token lifetime.
9. A delegated JWT uses the delegator as `sub`, the delegate as `act.sub`, and includes `delegation_id`, `client_id`, and `azp`. Multi-hop exchange is rejected and DPoP bridging follows client policy.

This is the correct identity model: the delegate stays authenticated as themselves, the delegator remains the subject of the operation, and both identities are available for audit.

## Target release contract

The first supported user-delegation release should make a deliberately narrow promise:

- One tenant, one delegator, one delegate, one bound confidential client.
- Explicit invitation and acceptance.
- One read-only `profile.read` capability over the delegator's own profile resource.
- Delegate remains actor; delegator remains subject.
- No role copying, tenant administration, consent, sessions, credentials, MFA, recovery, email changes, grant management, or multi-hop delegation.
- Every operation is authorized online by capability and resource, and audited with actor, subject, client, grant, resource, outcome, and correlation ID.
- Revocation effectiveness is stated precisely. Recommended initial bound: online validation on every API request, or opaque/introspected delegated tokens. Do not advertise immediate JWT revocation without that control.

## Completion plan

### Phase 0: freeze semantics and repair current invariants

**Goal:** make the existing foundation safe to extend.

1. Publish the target release contract above and keep `EnableDelegatedAccess` off by default outside explicit development/demo environments.
2. Make one request-scoped delegated-context resolver call the central authorization service for a concrete client, capability, and resource. Remove the weaker duplicate validation paths as authorization boundaries.
3. Re-check feature flag, tenant, memberships, client binding, grant state, capability, and resource on every delegated operation.
4. Apply normal public/pairwise subject identifier calculation to both `sub` and the chosen actor identifier policy.
5. Define delegated-token revocation behavior: prefer opaque/introspected tokens for the first release, or add an API-side online grant-state validator with a very short bounded cache.
6. Fix support-use metrics, update grant `LastUsedAt`/`UseCount`, hash identifiers consistently, and attach trace/correlation IDs.
7. Prevent or explicitly end concurrent support sessions for one platform administrator.

**Exit criteria:** negative unit tests prove tenant suspension, membership loss, client mismatch, resource mismatch, feature disablement, expiry, and revocation all deny the next operation.

### Phase 1: implement one real delegated resource operation

**Goal:** prove authorization, not just token shape.

Build a small `Profile Assistance` resource in the demo API:

- `GET /profiles/{subjectId}/summary` requires `profile.read`.
- The API validates issuer, audience, expiry, client, `sub`, `act.sub`, and `delegation_id`.
- The API resolves the internal grant or calls a dedicated IdP authorization/introspection endpoint.
- Authorization passes the concrete resource `user:{subjectId}` to the central delegated authorization policy.
- A normal user may read only their own profile; a delegated caller may read only the delegator resource in the grant.
- The response returns useful profile data plus a compact audit reference, not raw token diagnostics.

Keep write capabilities out of this phase. `profile.update_limited` needs field-level policy, step-up authentication, and a separate security review.

**Exit criteria:** the same delegate token is denied for another user, another client, another tenant, an ungranted capability, an expired/revoked grant, and after either membership is suspended.

### Phase 2: turn the existing RazorClient sample into a coherent demo

**Goal:** demonstrate the complete human workflow without copying IDs.

1. Add a **Delegated tasks** page to the RazorClient that lists accepted grants valid for that client and current user.
2. Replace manual grant-ID paste with an explicit grant selector showing delegator, capability, purpose, and expiry.
3. Exchange the signed-in delegate's token server-side and call the Profile Assistance endpoint.
4. Display “You are helping Alice” with actor, subject, bound client, capability, resource, and remaining time.
5. Add **Stop helping** to discard the delegated token/context without revoking the grant.
6. Add **Relinquish grant** for the delegate and retain **Revoke** for the delegator.
7. Demonstrate immediate/defined-bound revocation: keep the page open, revoke in the delegator session, retry, and show a controlled denial.
8. Keep a separate diagnostics disclosure for token claims; claims should support the story rather than be the task itself.

**Demo script:**

1. Alice creates a one-hour `profile.read` grant for Bob, bound to RazorClient.
2. Bob receives the MailHog invitation, reviews the exact authority, and accepts.
3. Bob signs into RazorClient as Bob, selects Alice's accepted task, and reads Alice's profile summary.
4. Bob cannot read Carol's profile or mutate Alice's profile.
5. A second client cannot use the grant.
6. Alice revokes the grant; Bob's next API request is denied within the documented revocation bound.
7. Audit history shows Alice as subject, Bob as actor, RazorClient as client, the profile resource, and both successful and denied attempts.

### Phase 3: finish Tenant Support Access operations

**Goal:** make privileged support access controllable by operations and incident responders.

1. Add an active-session view with actor, tenant, reason, ticket, start, expiry, and status.
2. Allow another currently authorized platform administrator to revoke a session with a required reason.
3. Add per-actor concurrent-session policy and transaction-safe enforcement.
4. Add full authorization-handler tests for role removal, tenant suspension, actor mismatch, expiry, revocation, read allow, and write deny.
5. Add endpoint inventory tests for all tenant-admin Razor handlers and Minimal APIs; fail when a new endpoint lacks an operation classification.
6. Remove remaining active “impersonation” labels and filenames after the compatibility window, retaining the term only in migration/history notes.
7. Add expiry warnings and audit/session links to the banner and history UI.

### Phase 4: operationalize and expand cautiously

**Goal:** make the feature supportable before adding authority.

1. Add counters and latency for grant create, accept, decline, activate, authorize, deny, revoke, expire, exchange, and resource use.
2. Add alerts for denial spikes, repeated wrong-user invitation use, client mismatches, and support-session write attempts.
3. Document retention, privacy treatment, incident revocation, feature rollback, and the exact token revocation bound.
4. Add concurrency tests for accept-versus-revoke and duplicate invitation consumption.
5. Add desktop/mobile E2E coverage for create, accept, use, wrong resource, wrong client, exit, expiry, membership loss, and revocation.
6. Run migration upgrade/rollback tests against a populated pre-feature database.
7. Require a capability-specific threat model and security approval before enabling any new capability, especially writes.

## Test strategy

| Layer | Required proof |
|---|---|
| Domain | Valid lifecycle transitions, invitation single use, concurrency, lifetime, immutable client/tenant/users |
| Authorization | Deny-by-default matrix for actor, subject, client, tenant, membership, capability, resource, time, status |
| Protocol | Scope/audience intersection, pairwise subjects, DPoP, single-hop, opaque/JWT behavior, revoked grant |
| Resource API | Actual object-level authorization and online revocation behavior |
| Browser | Explicit acceptance/activation/exit, dual-identity display, no silent authority switch |
| Support access | Full handler behavior and endpoint inventory, including all unsafe methods |
| Operations | Audit reconstruction, metrics, alerts, retention, and feature-disable rollback |

## Definition of done

- A delegated grant authorizes at least one real client resource operation and no broader operation.
- The resource server validates capability, resource, current grant state, and both identities; it does not rely only on claims display.
- Revocation behavior for issued tokens is enforced and documented with a measurable upper bound.
- Normal and pairwise subject identifier rules are preserved.
- Browser, token, API, and audit paths agree on actor, subject, tenant, client, grant, capability, and resource.
- Support access can be independently revoked, has tested endpoint coverage, and remains read-only.
- Negative tests cover every trust boundary and outnumber or match positive delegated-authorization tests.
- Feature flags, dashboards, alerts, runbooks, privacy/retention guidance, and migration tests are complete.
- A focused security review approves `profile.read` before production enablement.

## Recommended implementation order

Start with Phase 0 and the read-only Profile Assistance operation. Do not add more capabilities until that operation proves object-level authorization and revocation end to end. Then polish the existing RazorClient into the user-facing demo, finish support-session operations, and only afterward evaluate limited write delegation.