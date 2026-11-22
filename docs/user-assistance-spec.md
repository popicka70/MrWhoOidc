# Consent-Based User Assistance — Implementation Specification

Status: implemented
Date: 2026-11-22
Owner: Identity Platform / Account Self-Service
Related: [Real user impersonation](reference/user-impersonation.md), [Delegated and support access](reference/delegated-and-support-access.md)

## 1. Summary

Add a **user-initiated, consent-based assistance session**. A signed-in tenant user can ask another
eligible user for temporary help. The helper accepts the request, then temporarily acts as the requester
through the existing overlay-principal machinery. The requester remains the subject (`sub`) and is the
person who granted access; the helper is the actor (`act.sub`). The requester can end the session at any
time.

This is deliberately separate from **platform-admin impersonation**:

- Admin impersonation is started by a platform administrator without the target user's consent. It remains
  available for platform support, access-right debugging and incident response.
- User assistance is started by the subject user. It is consent-based, helper-role-gated, tenant-scoped,
  visible to both parties and revocable by the requester.
- Support access remains the tenant-scoped, read-only platform-admin path.

All three features may share the overlay, target OP session and revocation mechanisms, but their
authorization rules, UI, audit events and token metadata stay distinct.

## 2. Goals

1. A tenant user can create an assistance request from their account and choose an eligible helper.
2. The helper is notified in-app and by email, must sign in, and must explicitly accept.
3. While active, the helper sees the requester's identity on normal WebAuth/tenant surfaces and in OIDC
   tokens, subject to the configured rights mode and read-only policy.
4. The requester can terminate the session immediately at any time. Expiry and helper/manager revocation
   also terminate it.
5. Helper eligibility and read-only/read-write capability are controlled by tenant roles.
6. The tenant can choose whether the helper receives the requester's full role set or the intersection of
   the requester's and helper's roles.
7. Ending or expiring a session revokes the target OP session and its refresh tokens.
8. Every request, acceptance, decline, start, end, revocation, expiry and validation failure is auditable.
9. The existing admin impersonation feature is unchanged.

## 3. Non-goals

- Replacing platform-admin impersonation or tenant support access.
- Cross-tenant assistance in the first version. Helpers must be members of the requester's tenant.
- Letting a requester grant access to an arbitrary unauthenticated link holder. The email link only
  identifies a request; acceptance requires a signed-in, explicitly invited helper.
- Guessing or delegating arbitrary OAuth scopes. The session assumes the requester's identity; access is
  role-based, not scope-based.
- Making an arbitrary relying party obey a read-only marker. RPs must honor the advertised claim; tenants
  that need a hard guarantee can disable token issuance for read-only sessions.
- Reusing the legacy `ImpersonationAuditLogs` table.

## 4. Definitions

- **Requester / subject** — the signed-in tenant user who asks for help and grants temporary access. The
  session's `sub` is the requester.
- **Helper / actor** — the invited tenant user who accepts and acts as the requester. The session's
  `act.sub` is the helper's user-account id.
- **Assistance request** — a pending consent record with requester, helper, requested mode, reason and
  acceptance deadline.
- **Assistance session** — the active, durable record created when the helper accepts. It contains the
  target OP session and all lifecycle timestamps.
- **Read-only mode** — the helper may inspect the requester's normal pages and endpoints, but the OP
  rejects unsafe HTTP methods on overlaid requests.
- **Read-write mode** — the helper may perform the same actions as the requester, subject to the requester's
  effective roles and the exclusion of platform-admin control surfaces.
- **Full target rights** — the helper's overlay principal carries all of the requester's roles.
- **Intersection rights** — the helper's overlay principal carries only the roles held by both the
  requester and the helper in the requester's tenant.

## 5. Roles and tenant settings

### 5.1 Global feature flag

`AuthOptions.EnableUserAssistance` (configuration key `Auth:EnableUserAssistance`) defaults to `false`.
The development compose stack and the E2E environment enable it explicitly. When false, all assistance
pages and endpoints report that the feature is disabled.

### 5.2 Tenant settings

Add an `AssistanceTenantSettings` section to `TenantSettings` (stored in `Tenant.SettingsJson`):

```json
{
  "assistance": {
    "enabled": true,
    "rightsMode": "intersection",
    "requesterRoles": [],
    "readOnlyHelperRoles": ["tenant-admin"],
    "readWriteHelperRoles": [],
    "managerRoles": ["tenant-admin"],
    "readOnlyTokenPolicy": "markerOnly",
    "defaultDurationMinutes": 15,
    "maxDurationMinutes": 60,
    "requestLifetimeMinutes": 10
  }
}
```

All values are optional and fall back to safe defaults:

| Setting | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` when the global flag is on | Tenant-level disable switch |
| `rightsMode` | `intersection` | `fullTarget` or `intersection` |
| `requesterRoles` | empty | Empty means any authenticated tenant user may request |
| `readOnlyHelperRoles` | `["tenant-admin"]` | Roles allowed to accept read-only requests |
| `readWriteHelperRoles` | empty | Roles allowed to accept writable requests; also implies read-only |
| `managerRoles` | `["tenant-admin"]` | Roles allowed to list/revoke all tenant assistance sessions |
| `readOnlyTokenPolicy` | `markerOnly` | `markerOnly` or `deny` |
| `defaultDurationMinutes` | `15` | Initial session duration after acceptance |
| `maxDurationMinutes` | `60` | Maximum requested duration |
| `requestLifetimeMinutes` | `10` | How long an unanswered request stays pending |

Role names are resolved in the requester's tenant realms. Roles are assigned through the existing role
administration UI; the assistance feature does not create a new permission table. A role name may be used
by more than one feature (for example, `tenant-admin` can be a default read-only helper role).

The tenant settings are snapshotted onto the request/session (`rightsMode`, `requestedMode`, durations) so a
later settings change does not silently alter an active session.

### 5.3 Role rules

- A helper must hold at least one configured read-only or read-write helper role at acceptance time and on
  every request. Losing the role ends the session.
- A read-write role implies read-only capability.
- A requested read-write session requires a helper with a read-write role. If the helper only has a
  read-only role, the request cannot be accepted in write mode; the helper may accept a read-only version
  only if the requester requested read-only.
- The requester must be active, a member of the tenant, and hold a configured requester role when
  `requesterRoles` is non-empty.
- A user cannot request assistance from themselves.
- A user with an active admin impersonation or tenant support-access session cannot start or accept an
  assistance session. The inverse also applies: an active assistance session blocks starting admin
  impersonation and support access.
- Only one active assistance session may exist for a requester at a time. A helper may have at most one
  active assistance session at a time.

## 6. Rights mode

### 6.1 Full target rights (`fullTarget`)

The overlay principal carries all of the requester's roles in the requester's tenant. This reproduces the
requester's normal access exactly and is the right choice when the purpose is to reproduce a user-specific
problem. Platform-admin control surfaces remain blocked by the control-path rule.

### 6.2 Intersection rights (`intersection`)

The overlay principal carries `requester roles ∩ helper roles` in the requester's tenant. The helper can
never receive a role they do not already hold. Consequences:

- The helper may see less than the requester. This is intentional and must be shown in the UI.
- If the requester's role set changes, the effective set is recomputed on the next request and on token
  issuance.
- If intersection yields no roles, the session still permits authenticated base pages, but role-gated pages
  and role claims are absent.
- Role names are compared within the requester's tenant realms. Platform-realm roles are not mixed into
  tenant role claims. Platform administrators continue to use admin impersonation rather than consent
  assistance in the first version.

The chosen mode is stored on the request and session and is enforced for WebAuth authorization and token
role claims.

## 7. Read-only mode

### 7.1 WebAuth and endpoint enforcement

When an active overlay has `Mode = ReadOnly`:

- `GET`, `HEAD` and `OPTIONS` requests are allowed.
- `POST`, `PUT`, `PATCH` and `DELETE` are rejected with `403` on all overlaid paths.
- Control paths (`/assistance/*`, `/account/assistance/*`, `/impersonation/*`, `/platform-admin/*`, login,
  logout, MFA, tenant selection, external login, end-session) are not overlaid and remain available to the
  actor.
- The helper may always end the session through the control surface.
- Read-only enforcement is fail-closed: unknown endpoints that require a write method are denied.

This blocks writes to account pages, tenant-admin pages, and API endpoints exposed through the overlaid
session. Read-only users are expected to inspect, not fill in forms.

### 7.2 Token policy

A read-only session is a property of the OP-hosted session. Once an OIDC token is issued, an arbitrary
relying party cannot be forced to enforce it. The tenant setting `readOnlyTokenPolicy` controls this:

- **`markerOnly`** (default) — tokens are issued with `assistance_mode = "read_only"`. Relying parties that
  need strict read-only behavior must honor this claim. The OP still blocks writes on its own surfaces.
- **`deny`** — a read-only session cannot complete `/authorize`; the helper must request read-write
  assistance or use the OP-hosted surfaces only.

Read-write sessions issue tokens with `assistance_mode = "read_write"` and the effective role set.

## 8. User flows

### 8.1 Request

1. Requester opens **Account → Assistance**.
2. The page lists eligible helpers: active tenant users holding a configured helper role. Each row shows
   whether the helper can accept read-only, read-write or both.
3. Requester chooses a helper, requested mode, reason (required, max 500), optional ticket reference
   (max 100), and duration (clamped to `1..maxDurationMinutes`, default `defaultDurationMinutes`).
4. Requester submits. The page shows a confirmation summary:
   - helper name;
   - tenant;
   - read-only or read-write;
   - full-target or intersection rights;
   - expiry.
5. The service creates a `Pending` assistance request with a one-time invitation token hash and sends:
   - an in-app inbox item for the helper;
   - an email containing `/account/assistance/accept?requestId=<id>&token=<one-time-token>`.
6. The requester sees the pending request and may cancel it before acceptance.

The request itself is the requester's consent. No second requester approval is required for acceptance.

### 8.2 Accept or decline

1. The helper opens the in-app inbox or the email link.
2. If not signed in, the link redirects to the normal login flow with a return URL. The helper must
   authenticate as the invited identity.
3. The accept page displays requester, tenant, requested mode, rights mode, reason and expiry.
4. On **Accept**:
   - validate the request is `Pending`, unexpired and unrevoked;
   - validate the signed-in helper matches the invited user/account;
   - validate the helper still holds the required role for the requested mode;
   - create the target OP session;
   - transition the request to `Active` with `SessionExpiresAt = now + requested duration`;
   - store the target OP session id, target security stamp and effective-rights mode;
   - set `HttpContext.Session["UserAssistanceSessionId"]` in the helper's browser session;
   - send an in-app/email notification to the requester that the helper accepted.
5. On **Decline**, mark the request `Declined` and notify the requester.

The invitation token is one-time and is cleared on acceptance/decline/cancel/expiry. The token alone cannot
accept a request without the matching signed-in helper.

### 8.3 Active session

- The helper's overlaid pages show a persistent banner:
  - requester name and username;
  - actor/helper name;
  - read-only or read-write;
  - full-target or intersection;
  - remaining time;
  - **End assistance** button.
- The requester's own pages show a separate banner:
  - helper name;
  - mode;
  - remaining time;
  - **End assistance** button.
- The requester can also end from the Assistance page.
- The helper can end the session voluntarily; this marks it `Ended`.
- A tenant manager can revoke an active session with a reason.
- The helper is not allowed to switch tenants, sign in as another user, use MFA or access platform-admin
  surfaces while the overlay is active.
- If the requester is deactivated, the tenant is disabled, the helper loses the required role, the target
  security stamp changes, or the target OP session is revoked, the next request ends the assistance
  session and returns the helper to their own identity.

### 8.4 End, expiry and cleanup

- Requester end: marks the request `Ended`, revokes the target OP session and its tokens, clears the
  helper's session key, and notifies the helper.
- Helper end: same, with the helper recorded as the ender.
- Manager revoke: marks `Revoked`, records the manager and reason, revokes the target session/tokens.
- Expiry: a background worker marks overdue requests `Expired` and overdue sessions `Expired`, revoking
  target sessions and tokens. Live requests also detect expiry.
- Pending requests that pass `requestLifetimeMinutes` become `Expired` and their invitation token is
  cleared.
- The requester's own authentication session is never revoked by ending assistance.

## 9. Claims and OIDC behavior

Authorization codes issued for an assistance session store the target OP session id. Token exchange
resolves the active assistance session by that `sid` and adds security metadata regardless of scope or
claim-mapping restrictions:

```json
{
  "sub": "<requester user id>",
  "name": "<requester name>",
  "act": { "sub": "<helper user account id>" },
  "assistance_id": "<assistance session id>",
  "assistance_mode": "read_only",
  "amr": ["pwd", "assistance"]
}
```

- `act` and `assistance_id`/`assistance_mode` are security metadata. They must not be removed by
  `RestrictIdTokenClaimsToClaimsRequest` or client claim restrictions.
- The ID token `sub`, `name`, `email`, roles, `sid`, `auth_time` belong to the requester.
- Role claims are the requester's roles in full-target mode and the effective intersection in intersection
  mode.
- Refresh tokens persist the target OP session id. Refreshed access tokens must continue to carry the
  assistance metadata; ending the session revokes the refresh tokens and makes further refresh attempts
  fail with `invalid_grant`.
- Read-only sessions follow the tenant's `readOnlyTokenPolicy`: `markerOnly` emits the marker; `deny`
  blocks authorization-code issuance.
- Opaque tokens may omit `assistance_id`/`assistance_mode` in the first version; the demo apps and E2E
  coverage use JWT access tokens. This is documented as a limitation.
- The existing admin impersonation claims (`act` + `impersonation_id`) remain unchanged.

## 10. Data model and persistence

Use a dedicated `UserAssistanceSession` table so the admin-impersonation record stays separate.

```text
UserAssistanceSession
  Id                          Guid PK
  TenantId                    Guid
  RequesterUserId             Guid
  RequesterUserAccountId      Guid
  RequesterUsername           string
  RequesterDisplayName        string?
  HelperUserId                Guid
  HelperUserAccountId         Guid
  HelperUsername              string
  HelperEmail                 string?
  Mode                        enum ReadOnly | ReadWrite
  RightsMode                  enum FullTarget | Intersection
  Status                      enum Pending | Active | Ended | Expired | Revoked | Declined | Cancelled
  Reason                      string(500)
  TicketReference             string(100)?
  CreatedAt                   DateTimeOffset
  ExpiresAt                   DateTimeOffset  // acceptance deadline while Pending
  AcceptedAt                  DateTimeOffset?
  SessionExpiresAt            DateTimeOffset? // duration deadline while Active
  EndedAt                     DateTimeOffset?
  EndedByUserAccountId        Guid?
  EndReason                   string(200)?
  TargetOpSessionId           Guid?
  TargetSecurityStamp         string?
  ActorAcr                    string?
  ActorAmrJson                string?
  ActorIdp                    string?
  InvitationTokenHash         string?
  CreatedFromIpHash           string?
  UserAgentHash               string?
  LastSeenAt                  DateTimeOffset?
  ConcurrencyToken            Guid
```

Indexes:

- `(TenantId, Status, SessionExpiresAt)`
- `(HelperUserAccountId, Status)`
- `(RequesterUserId, Status)`
- unique or filtered lookup on `InvitationTokenHash`

The EF migration is additive. Existing `UserImpersonationSessions`, `OpSessions`, `TenantSupportAccessSessions`
and token tables are unchanged.

The helper's target OP session is created only on acceptance. The requester's own OP session is never
reused as the target session.

## 11. Services and middleware

### 11.1 `IUserAssistanceService`

- `CreateRequestAsync(...)` — validates the requester, helper eligibility, mode, roles and limits; writes
  a pending request; sends inbox/email notification.
- `AcceptAsync(...)` — validates token, helper, role, request state and expiry; creates the target OP
  session and active session; sets the helper session key.
- `DeclineAsync(...)` — marks a pending request declined.
- `CancelAsync(...)` — requester cancels a pending request.
- `EndAsync(...)` — requester/helper/manager end or revoke.
- `GetInfoAsync(HttpContext)` — helper overlay info from the session key.
- `GetRequesterInfoAsync(HttpContext)` — active outgoing session for the requester banner.
- `ListForRequesterAsync(...)`, `ListForHelperAsync(...)`, `ListForManagerAsync(...)`.
- `ResolveOverlayAsync(...)` — builds the target principal for the helper.

### 11.2 `UserAssistanceMiddleware`

Runs after authentication and after `UserImpersonationMiddleware`, before tenant membership and
authorization.

1. Skip control paths and unauthenticated requests.
2. If an admin-impersonation overlay is already installed, skip (mutually exclusive).
3. Read `HttpContext.Session["UserAssistanceSessionId"]`. No value: continue.
4. Load the durable session and validate actor, status, expiry, helper role, tenant, requester, target
   security stamp and target OP session.
5. Validate the current tenant matches the session tenant.
6. Build the overlay principal with the requester's identity and effective roles.
7. In read-only mode, reject unsafe methods unless the path is a control path.
8. Install the principal, cache the info in `HttpContext.Items`, and update `LastSeenAt` at most once per
   minute.

The existing `UserImpersonationMiddleware.IsControlPath` helper is shared. Add `/assistance` and
`/account/assistance` to the control-path list. The two middlewares must be ordered deterministically and
must not both install an overlay.

### 11.3 Role resolution

- Helper eligibility and requester eligibility are resolved from role assignments in the requester's
  tenant realms.
- In intersection mode, effective roles are the intersection of requester and helper role ids (or names
  within the same realm).
- The overlay principal carries a custom effective-role marker so authorization handlers can enforce the
  intersection without re-querying both users on every check.
- `TenantAdminAuthorizationHandler` and any other role-based handler must honor the effective-role marker
  when an assistance overlay is active. The platform-admin handler must never be satisfied by the target
  user's tenant roles.
- Token claim builders filter role claims to the effective set in intersection mode.

### 11.4 Read-only filter

Add a global check after the overlay for unsafe HTTP methods in read-only mode. Reuse and generalize the
existing `SupportAccessReadOnlyPageFilter` conventions where possible. The check must cover Razor pages,
controllers and minimal API endpoints. Control paths and the assistance end/decline endpoints are exempt.

## 12. UI

### 12.1 Account → Assistance

New Razor pages under `Pages/Account/Assistance/`:

- `Index` — requester form, eligible helper list, pending requests, active outgoing session, history.
- `Accept` — helper acceptance/decline page with token validation.
- `Inbox` — helper pending requests and active sessions (or a shared page with role-aware sections).

The account tab bar adds an **Assistance** link. The page must be available to any authenticated tenant
user; role checks happen server-side.

### 12.2 Banners

- `_UserAssistanceBanner.cshtml` — helper overlay banner, included in the shared layout on overlaid pages.
- `_UserAssistanceRequesterBanner.cshtml` — requester banner, shown when the requester has an active
  outgoing session.
- Both banners use actor-bound antiforgery tokens for their control posts, following the existing
  `UserImpersonationAntiforgery` pattern.

### 12.3 Tenant admin settings

Add an **Assistance** section to `/Admin/Settings`:

- enabled (tenant override);
- rights mode select;
- requester roles (comma-separated role names; empty = all);
- read-only helper roles;
- read-write helper roles;
- manager roles;
- read-only token policy;
- default/max duration and request lifetime.

Validate role names against the tenant's roles and reject unknown names. The global feature flag still
gates the whole feature.

### 12.4 Manager history (optional first version)

If the manager role is configured, add `/Admin/Assistance` listing requests/sessions with status, actor,
subject, mode, reason and timestamps, plus a revoke action with a reason. This can ship after the core
requester/helper flow if time is constrained; the role and service support must exist from the start.

## 13. Notifications and email

- Reuse the existing mail service and templates. Add an assistance request template with requester name,
  tenant, mode, reason, expiry and an accept link.
- The link carries the request id and a one-time token. The token is hashed at rest; the plaintext exists
  only in the email and the requester/helper UI.
- If mail is disabled, the in-app inbox is still the primary notification channel.
- Add an acceptance notification to the requester (in-app and email when available).
- Do not include sensitive data beyond the requester's display name, mode, reason and expiry in the email.

## 14. Audit and metrics

Audit events via `IAuditSink`:

- `user_assistance.requested` — request id, requester, helper, tenant, mode, rights mode, reason, expires.
- `user_assistance.accepted` — request id, session id, requester, helper, mode, IP/user-agent hashes.
- `user_assistance.declined` — request id, helper.
- `user_assistance.cancelled` — request id, requester.
- `user_assistance.ended` — session id, requester, helper, duration, ender.
- `user_assistance.revoked` — session id, requester, helper, manager, reason.
- `user_assistance.expired` — request/session id, requester, helper.
- `user_assistance.validation_failed` — session id, reason (`actor_mismatch`, `role_missing`,
  `requester_inactive`, `tenant_inactive`, `security_stamp_changed`, `target_session_invalid`, etc.).

Metrics mirror the impersonation metrics:

- `user_assistance.requests`, `.accepts`, `.declines`, `.cancels`, `.ends`, `.revocations`, `.expirations`,
  `.validation_failures`;
- `user_assistance.session_duration` histogram.

A background cleanup service handles pending-request expiry and active-session expiry. It is registered
alongside the existing impersonation cleanup service.

## 15. Security invariants

1. **Consent is explicit.** No active session without a pending request created by the requester and
   accepted by the invited helper.
2. **Helper identity is verified.** The email link alone never grants access; the signed-in helper must
   match the invited user/account.
3. **Role-gated.** Helper eligibility and read-only/read-write capability come from configured tenant
   roles. Losing the role ends the session.
4. **Requester controls the lifetime.** The requester can end immediately; the helper cannot extend the
   session without a new request.
5. **No platform-level escalation.** Platform-admin surfaces are never overlaid. Tenant roles from the
   requester are the maximum; helper roles only gate eligibility and intersection mode.
6. **Read-only is fail-closed on OP surfaces.** Unsafe methods are rejected in read-only mode. RP token
   enforcement is documented and configurable.
7. **Revocation is complete.** End/expiry/revoke ends the target OP session and its refresh tokens.
8. **Requester session is independent.** Ending assistance never revokes the requester's own OP session.
9. **One elevated context at a time.** Assistance, admin impersonation and tenant support access are
   mutually exclusive per user.
10. **Audit survives the session.** The durable request/session row and audit events remain after end or
    expiry.
11. **No cross-tenant helpers.** Helpers must be tenant members in the first version.

## 16. Tests

### 16.1 Unit tests

- Settings defaults and tenant overrides; invalid role names rejected.
- Request validation: inactive requester/helper, self-request, non-member, missing requester role,
  missing helper role, blank reason, duration clamping, duplicate active request, active support/admin
  session.
- Acceptance: token validation, expired request, wrong helper, wrong tenant, read-only helper accepting
  write, role lost between request and accept, one-time token.
- Overlay: target `sub`, helper `act.sub`, `assistance_id`, `assistance_mode`, amr/idp; actor mismatch,
  expired session, deactivated requester, changed security stamp, invalid target OP session.
- Read-only: safe methods allowed, unsafe methods denied, control paths exempt.
- Intersection: effective role intersection; helper cannot gain target roles; full-target mode includes
  all target roles; platform-admin surfaces remain blocked.
- Revocation: requester end, helper end, manager revoke, expiry; target OP session and refresh tokens
  revoked; requester's session untouched.
- Claims: code flow access/ID tokens, refreshed access tokens, read-only marker, deny policy, claim
  restrictions cannot remove metadata.
- Mutual exclusion with admin impersonation and support access.

### 16.2 E2E (Playwright)

Add `e2e/tests/test_user_assistance.py`:

1. Alice requests help from an eligible helper; the in-app inbox and MailHog email both contain the
   request.
2. Helper signs in, opens the email link, accepts; the helper sees the assistance banner and Alice's
   account identity.
3. RazorClient/OidcDemo receive Alice's `sub`/`name`, the helper's `act.sub`, `assistance_id` and
   `assistance_mode`.
4. Read-only mode rejects a profile write and allows reads.
5. Alice ends the session; the helper banner disappears, Alice's account shows Alice again, and the target
   refresh tokens are revoked.
6. Expiry ends the session and clears it from the helper.
7. Intersection mode: a helper without a target role cannot access the corresponding role-gated page; the
   token role claims are filtered.
8. Non-eligible helper cannot accept; wrong signed-in user cannot use the link; forwarded link alone fails.
9. Tenant admin settings can switch rights mode, roles and token policy.
10. Existing admin impersonation tests still pass unchanged.

Tests must use dedicated browser contexts and clean up pending/active requests in `finally`, as the
impersonation tests do.

## 17. Documentation and rollout

- Add `docs/reference/user-assistance.md` with the actor/subject model, roles, settings, read-only and
  intersection semantics, claims and security invariants.
- Update `docs/reference/delegated-and-support-access.md` comparison table.
- Update `docs/admin-guide.md` with the tenant settings and management flow.
- Update `docs/production-setup-guide.md` with the global flag and read-only token policy caveat.
- Update `docs/example-applications-guide.md` for assistance claims in the demo apps.
- Update `docs/index.md` and `e2e/TEST_SCENARIOS.md`.
- Enable the feature in development and E2E only; keep it off by default in production.
- Keep admin impersonation and tenant support access unchanged and independently flagged.

## 18. Implementation order

1. Spec and documentation skeleton.
2. Model, migration, settings types and defaults.
3. Store, service, role resolution and validation.
4. Middleware, read-only filter, effective-role enforcement and token claims.
5. Requester/helper UI, banners and email/inbox notifications.
6. Tenant settings UI.
7. Demo apps and TestApi claims.
8. Unit tests and targeted E2E.
9. Full E2E suite and documentation/rollout notes.

## 19. Decisions

1. **Separate feature, separate durable table.** Admin impersonation remains unchanged.
2. **Tenant-scoped helpers only in v1.** Platform-admin assistance can be added later through platform
   helper roles.
3. **Requester creates consent; helper must accept.** The email link is not a bearer grant.
4. **Role-based eligibility and mode.** Read-only and read-write roles are separate; writable implies
   read-only.
5. **Tenant rights mode.** `fullTarget` reproduces the requester exactly; `intersection` never exceeds the
   helper's roles.
6. **Read-only is enforced on OP surfaces and advertised to RPs.** Strict deployments use the `deny` token
   policy.
7. **Separate claims.** `assistance_id`/`assistance_mode` distinguish consented assistance from admin
   `impersonation_id`.
8. **Requester can always terminate.** The helper cannot extend or prevent termination.
