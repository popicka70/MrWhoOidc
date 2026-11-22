# User Impersonation — Implementation Specification

Status: implemented on `feat/tenant-user-impersonation`
Date: 2026-11-22
Owner: Platform Administration

## 1. Summary

MrWhoOidc already has two elevated-access features:

- **Tenant Support Access** lets a platform administrator browse one tenant in read-only mode. The platform
  administrator remains the authenticated actor and no user identity is assumed.
- **Delegated Access** lets one user act for another user through a specific client.

Neither is impersonation. Support Access has no user subject, and the OIDC `sub` seen by relying parties is
still the administrator's. The previous "impersonation" implementation (removed in `9f6f5796`) was also
tenant-level: it only added a session flag and granted tenant-admin authorization; it never changed the auth
principal, cookie, or token identity.

This specification defines **real user impersonation**: a platform administrator starts a bounded session in
which the browser and the OIDC provider assume the identity of one active user in one tenant. Relying parties
see the target user's `sub`, name, email, roles and OP session. The administrator's own identity is retained
out-of-band for authorization, audit, and the visible banner, and is exposed to relying parties through the
standard `act` claim.

## 2. Goals

1. A platform administrator can select an active user in any active tenant and start an impersonation session.
2. While the session is active, WebAuth pages, tenant admin pages, `/authorize`, consent, `/userinfo`, and
   issued access/ID tokens use the **target user** identity.
3. A relying party sees the target user's normal claims (`sub`, `name`, `email`, roles) and the target OP
   session `sid`, not the administrator's.
4. The platform administrator remains the accountable actor:
   - the durable session records actor and subject separately;
   - every request re-validates that the actor still has `platform-admin`;
   - tokens and audit carry an actor marker;
   - a persistent banner names both actor and subject.
5. The session is short-lived (15 minutes by default, clamped to 1–60), can be stopped immediately, and is
   revoked automatically on expiry.
6. Stopping or expiring the session revokes the impersonated OP session and the refresh tokens it produced.
7. Every start, stop, expiry and rejected validation is audited.

## 3. Non-goals

- Impersonating a user in a tenant that is inactive or deleted.
- Impersonating a deactivated user.
- Impersonating a user account without an active tenant membership.
- Letting a tenant administrator impersonate users. Only `platform-admin` can start a session.
- Replacing Tenant Support Access. Support Access remains the tenant-scoped, read-only path. User
  Impersonation is user-scoped and writable, because "acting as the user" includes the actions the target
  user could take. The banner and audit make that explicit.
- Impersonating a platform administrator's platform-realm identity or granting platform-admin surfaces to the
  target identity. Platform administration is never performed "as" the target.
- Changing delegated access semantics.

## 4. Definitions and security invariants

- **Actor**: the authenticated platform administrator. Account id = `UserAccount.Id`; user id =
  `User.Id` in the actor's home tenant.
- **Subject**: the target user. Account id = target `User.UserAccountId` (fallback `User.Id`); user id =
  target `User.Id`; tenant id = target `User.TenantId`.
- **Impersonation session**: the durable `UserImpersonationSession` row that binds actor, subject, target
  tenant, target OP session, lifetime and reason.

Invariants:

1. **Actor and subject stay distinct.** No code may overwrite the actor identity in the durable session or
   audit trail. `EffectiveAccessContext` reports `ActorUserAccountId = actor`, `SubjectUserAccountId =
   target account`, `Kind = UserImpersonation`.
2. **One elevated context at a time.** An actor with an active Support Access session cannot start
   impersonation, and an actor with an active impersonation session cannot start Support Access.
3. **Short and bounded.** Default duration 15 minutes; requested duration clamped to 1–60 minutes. The
   durable row is authoritative; the session cookie/session key is only a pointer.
4. **Re-validated per request.** The actor still has `platform-admin`; the tenant is active; the target user
   is active; the target account security stamp is unchanged; the target OP session is valid; the
   impersonation row is active and unexpired.
5. **No privilege escalation through the target.** If the target user lacks a role, impersonation does not
   add it. If the target user has platform-admin, platform-admin pages are still blocked for the
   impersonation path (section 7.4).
6. **Cross-tenant cookie validation does not sign the actor out.** The actor's auth cookie remains the
   admin cookie. The cookie validator explicitly permits it while a valid impersonation session references
   the actor, then `TenantMembershipMiddleware` sees the overlaid target principal.
7. **Revocation is complete.** End/expiry revokes the target OP session and its refresh tokens. Access
   tokens are short-lived JWTs and are not revoked individually.
8. **Audit is durable and independent of the live session.** History survives stop/expiry and does not rely
   on the stale legacy `ImpersonationAuditLogs` table.

## 5. User flow

### 5.1 Start

1. Platform administrator opens `/platform-admin/user-impersonation` from the platform-admin navigation.
2. The page lists active tenants and lets the administrator search users in the selected tenant by username,
   email or display name.
3. The administrator selects one active user, enters a **required reason** (max 500), an optional ticket
   reference (max 100), and a duration (default 15, max 60).
4. `POST` calls `IUserImpersonationService.StartAsync`. On success:
   - a target `OpSession` is created for the target user/tenant;
   - a `UserImpersonationSession` row is created with `Active` status;
   - `HttpContext.Session["UserImpersonationSessionId"]` points at the row;
   - the administrator is redirected to the target tenant landing page, or to a caller-supplied local
     `returnUrl` (for example, an admin page or an example app).
5. The banner appears immediately on every WebAuth page.

### 5.2 Active session

- The target principal is installed by middleware after authentication (section 7.2).
- Tenant pages and tenant admin pages authorize against the target user's roles.
- `/authorize` issues codes for the target user and the target OP session.
- Example apps receive the target identity and `act`/`impersonation_id` metadata.
- The banner shows target name/username, tenant, actor username, reason, ticket, remaining time, and an
  **End impersonation** button.
- The actor may still visit `/platform-admin` pages as themselves. This is explicit: platform-admin pages
  are not overlaid, and the banner remains visible. This lets the administrator inspect the session and stop
  it, but all platform-admin actions are performed by the actor, not the target.

### 5.3 Stop

1. The banner posts to `/impersonation/stop` (anti-forgery protected).
2. The page authorizes the actor using the normal `platform-admin` policy; `/impersonation/*` is excluded
   from overlay.
3. `StopAsync` marks the row `Ended`, revokes the target OP session and its refresh tokens, emits audit and
   metrics, removes the session key, and clears the preferred-tenant session values.
4. The administrator is redirected to `/platform-admin/user-impersonation` with a success message.

### 5.4 Expiry and invalidation

- A background service marks expired rows `Expired` and revokes the target OP session/tokens.
- The overlay also detects expiry/invalidation on the next request, marks the row and revokes the target
  session (best effort), removes the session key, and continues without the overlay. The actor stays signed
  in as themselves.
- Target user deactivation, target account security-stamp rotation, tenant deactivation, actor role removal,
  or target session revocation all invalidate the session immediately.

## 6. Data model

### 6.1 `UserImpersonationSession`

New entity in `MrWhoOidc.Auth/Persistence/UserImpersonationSession.cs`:

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `Guid` PK | `GuidHelper.NewId()` |
| `ActorUserAccountId` | `Guid` | Global account id of the platform administrator |
| `ActorUserId` | `Guid` | Per-tenant user id from the auth cookie (`NameIdentifier`) |
| `ActorUsername` | string(200) | Denormalized for audit/history |
| `TenantId` | `Guid` | Target tenant, FK to `Tenants` (Restrict) |
| `TargetUserId` | `Guid` | Target per-tenant `User.Id` |
| `TargetUserAccountId` | `Guid` | Target global account id |
| `TargetUsername` | string(200) | Denormalized |
| `TargetDisplayName` | string(200)? | Denormalized |
| `TargetSecurityStamp` | string(200)? | Security stamp at start; mismatch ends the session |
| `TargetOpSessionId` | `Guid` | OP session created for the target; unique index |
| `Status` | enum | `Active`, `Ended`, `Expired`, `Revoked` |
| `Reason` | string(500) | Required |
| `TicketReference` | string(100)? | Optional |
| `CreatedAt` | `DateTimeOffset` | Start time |
| `ExpiresAt` | `DateTimeOffset` | Bounded lifetime |
| `EndedAt` | `DateTimeOffset?` | Stop/expiry |
| `RevokedAt` | `DateTimeOffset?` | Explicit revocation |
| `RevokedByUserAccountId` | `Guid?` | Revoker |
| `RevocationReason` | string(500)? | Required for revoke |
| `LastSeenAt` | `DateTimeOffset?` | Throttled heartbeat |
| `CreatedFromIpHash` | string(64)? | Hashed, never raw IP |
| `UserAgentHash` | string(64)? | Hashed |
| `ConcurrencyToken` | `Guid` | Optimistic concurrency |
| `ActorAcr` | string(100)? | Actor's ACR, propagated to target session |
| `ActorAmrJson` | string(200)? | Actor's AMR values, space-delimited |
| `ActorIdp` | string(100)? | Actor's IdP, if any |

Status enum:

```csharp
public enum UserImpersonationStatus
{
    Active = 0,
    Ended = 1,
    Expired = 2,
    Revoked = 3
}
```

Indexes:

- `(ActorUserAccountId, Status, ExpiresAt)`
- `(TenantId, Status, ExpiresAt)`
- `(Status, ExpiresAt)`
- unique `(TargetOpSessionId)`

The entity is tenant-filtered with `ApplyRequiredTenantFilter<UserImpersonationSession>` like Support Access.
Every lookup that must work from a platform-admin/tenantless request uses `IgnoreQueryFilters()` and then
verifies `TenantId`, actor and subject explicitly. Writes from a start/stop request open
`TenantFilterScope.BeginCrossTenantScope()` because the actor is authorized to act for another tenant.

### 6.2 Store/service split

- `MrWhoOidc.Auth/Services/Impersonation/IUserImpersonationStore.cs` and
  `UserImpersonationStore.cs`: create, get by id (global), update with concurrency, list history, find active
  for actor, expire, revoke target OP session and refresh tokens.
- `MrWhoOidc.WebAuth/Services/UserImpersonationService.cs`: HTTP-facing start/stop/info/revoke logic,
  authorization checks, audit, metrics, session key management. Mirrors `TenantSupportAccessService` but
  with actor/subject separation.

The store's `RevokeTargetSessionAsync` runs in an explicit cross-tenant scope, loads the target `OpSession`
with `IgnoreQueryFilters`, marks it revoked, and sets `RevokedAt` on all refresh tokens whose `SessionId`
matches. It must not revoke other tokens of the target user.

### 6.3 Migration

Add EF migration `AddUserImpersonationSessions` after the model change. Do not modify or delete the legacy
`ImpersonationAuditLogs` table in this change; it stays dormant and is documented as legacy.

## 7. Runtime integration

### 7.1 Configuration

Add `AuthOptions.EnableUserImpersonation` (default `false`). Enable it for local development and the E2E
stack via `Auth:EnableUserImpersonation=true` in `appsettings.Development.json` and
`Auth__EnableUserImpersonation: "true"` in the WebAuth services of `docker-compose.dev.yml`. The start page
renders a "feature disabled" notice when false, and the service refuses to start.

### 7.2 `UserImpersonationMiddleware`

New middleware in `MrWhoOidc.WebAuth/Middleware/UserImpersonationMiddleware.cs`, registered in
`PipelineExtensions` **after `UseAuthentication()` and before `UseTenantMembership()`**.

Algorithm:

1. If the path is a control/auth/platform path (`/platform-admin`, `/impersonation`, `/login`, `/logout`,
   `/account/logout`, `/mfa`, `/switch-tenant`, `/auth/external`, `/signin-oidc`), continue without overlay.
2. Read `HttpContext.Session["UserImpersonationSessionId"]`. No value: continue.
3. Load the durable row globally. If missing, inactive or expired: remove the key and continue.
4. Require `tenantAccessor.CurrentTenant?.TenantId == row.TenantId`. If not, continue without overlay;
   `TenantMembershipMiddleware` will keep the admin out of the wrong tenant.
5. Validate the current authenticated principal is the actor:
   - `NameIdentifier == row.ActorUserId`;
   - `UserAccountId` claim, when present, matches `row.ActorUserAccountId`;
   - `IAuthorizationService.AuthorizeAsync(context.User, "platform-admin")` succeeds.
6. Load target user and account with `IgnoreQueryFilters`, explicitly pinned by id and tenant. Require:
   - target user active;
   - tenant active;
   - target account exists;
   - account security stamp equals `row.TargetSecurityStamp` (when the row stored one).
7. Validate the target OP session with `IOpSessionService.CheckGlobalAsync(row.TargetOpSessionId)`; require
   `Valid`. This also refreshes the target session's idle deadline.
8. Build the target principal:
   - `NameIdentifier` = target user id;
   - `Name` = target username;
   - `UserAccountId` = target account id;
   - `auth_time` = row.CreatedAt (Unix seconds);
   - `acr` = row.ActorAcr (fallback `urn:mrwho:impersonation`);
   - `amr` = actor's AMR values plus `impersonation`;
   - `idp` = `impersonation`;
   - `sid` = target OP session id (`N` format);
   - `mrwho:sec_stamp` = target account security stamp (when present);
   - `tenant_id` = target tenant id;
   - `urn:mrwho:impersonation-session-id` = row id;
   - `urn:mrwho:impersonator-user-account-id` = actor account id;
   - `urn:mrwho:impersonator-user-id` = actor user id.
   Authentication type: `"impersonation"`.
9. `context.User = new ClaimsPrincipal(...)`; cache the info in `HttpContext.Items`.
10. Update `LastSeenAt` at most once per minute.

Invalidation in steps 3/5/6/7 calls `EndAsync(row, Expired/Revoked, reason)` best-effort (which revokes the
target session), clears the session key, and continues as the actor. It never signs the actor out.

### 7.3 Cookie validation

`OpSessionCookieValidator.ValidateAsync` must not reject the actor's cookie merely because the request is for
the target tenant.

Before the normal tenant-bound `CheckAsync`:

1. Read `HttpContext.Session["UserImpersonationSessionId"]`.
2. Load the row globally and check:
   - row is active and unexpired;
   - `row.ActorUserId` equals the cookie principal's `NameIdentifier`;
   - `row.ActorUserAccountId` matches the `UserAccountId` claim when present.
3. If the impersonation row is valid, call the new
   `IOpSessionService.CheckGlobalAsync(adminSid, ct)`:
   - valid: return without the tenant-bound check (the overlay will replace the principal);
   - invalid: fall through to the existing rejection path.
4. If the impersonation row is stale but the admin OP session is globally valid, skip the tenant-bound check
   and let the overlay/TenantMembership handle it. This preserves the actor's sign-in while the UI reports
   the session as no longer active. Do not grant target access.

`IOpSessionService.CheckGlobalAsync` is a new method that looks up by session id with `IgnoreQueryFilters`,
evaluates revocation/absolute/idle expiry, and touches `LastSeenAt` with the same one-minute throttle as
`CheckAsync`. It is used only for impersonation-related session validation.

`SecurityStampCookieValidator` continues to validate the actor's account stamp. The overlay separately
validates the target account stamp.

### 7.4 Authorization

- Start/stop/history pages use `[Authorize(Policy = "platform-admin")]` and
  `[RequireDefaultTenantContext]` where appropriate.
- Platform-admin pages must not be overlaid. If the target user happens to have platform-admin, the
  impersonation path still cannot use platform-admin surfaces because the overlay is skipped there and the
  actor is the one authorized.
- Tenant admin authorization uses the overlaid target `NameIdentifier` and DB roles. If the target is not a
  tenant admin, tenant-admin pages return access denied; the target's account pages remain available.
- `PlatformAdminAuthorizationHandler`/`TenantAdminAuthorizationHandler` need no role-claim changes: both read
  the principal's user id and query DB roles.

### 7.5 Access context

Extend `EffectiveAccessContext`:

```csharp
public sealed record EffectiveAccessContext(
    Guid ActorUserAccountId,
    Guid SubjectUserAccountId,
    Guid TenantId,
    AccessContextKind Kind,
    Guid? SupportAccessSessionId,
    Guid? DelegatedAccessGrantId,
    Guid? ImpersonationSessionId = null);
```

Add `AccessContextKind.UserImpersonation = 3`.

`EffectiveAccessContextAccessor` evaluates User Impersonation before Support Access (they are mutually
exclusive) using the `urn:mrwho:impersonation-session-id` and actor claims on the overlaid principal, or the
session key when called on a control path. It returns:

- actor = `urn:mrwho:impersonator-user-account-id`;
- subject = target `UserAccountId` from the row;
- tenant = row tenant;
- kind = `UserImpersonation`;
- `ImpersonationSessionId = row.Id`.

Normal and delegated behavior is unchanged.

### 7.6 Token behavior

- `/authorize` uses the overlaid target principal, so authorization codes carry `UserId = target user id`.
- `AuthorizationMetadataService` stores the target OP session id in `AuthorizationCode.Sid` because the
  overlaid principal carries the target `sid`.
- `AuthorizationCodeExchanger` adds, when the code's `Sid` matches an active `UserImpersonationSession`
  `TargetOpSessionId`:
  - access token and ID token claim `act` = JSON `{"sub":"<actor account id>"}` with
    `JsonClaimValueTypes.Json`;
  - access token and ID token claim `impersonation_id` = row id.
  Claims are emitted regardless of scope, because they are security metadata. They must not override a
  client's `RestrictIdTokenClaimsToClaimsRequest` handling for the target user's normal claims.
- ID token `sub`, `name`, `email`, `roles`, `sid`, `auth_time` are the target's. The administrator's id is
  never used as `sub`.
- Refresh tokens persist `SessionId = target OP session id`. Ending the impersonation revokes them.
- Implicit flow (`ImplicitTokenService`) gets the same `act`/`impersonation_id` treatment if it is used with
  impersonation; code flow is the required E2E path.
- Opaque access tokens may omit `act`; TestApi and the demo apps use JWT access tokens. Document this as a
  known limitation if an opaque-token client is used during impersonation.

### 7.7 Audit, metrics, cleanup

Audit events via `IAuditSink`:

- `user_impersonation.started`: session id, actor account/user id, target account/user id, tenant id, reason,
  ticket, expires_at.
- `user_impersonation.ended`: session id, actor, target, duration, end reason.
- `user_impersonation.expired`: session id, actor, target, expiry.
- `user_impersonation.revoked`: session id, actor, target, revoker, reason.
- `user_impersonation.validation_failed`: session id, actor id, reason (`session_not_found`,
  `actor_mismatch`, `platform_admin_role_missing`, `tenant_inactive`, `target_inactive`,
  `security_stamp_changed`, `target_session_invalid`, `expired`), path.

Metrics (new `IUserImpersonationMetrics` or reuse a simple meter):

- starts, stops, expirations, validation failures (tagged by reason), duration histogram.

Background `UserImpersonationCleanupService` runs every minute, loads active rows past `ExpiresAt` with
`IgnoreQueryFilters`, marks them `Expired`, and revokes target sessions. Log and audit each transition once.

### 7.8 Sign-out and tenant switching

- `/logout` and `/account/logout` first end any active impersonation (mark ended/revoke target session) and
  then proceed with the normal sign-out. If the implementation cannot safely continue the sign-out, it may
  redirect to the stop confirmation instead; it must never leave an active impersonation row pointing at a
  signed-out actor.
- Tenant switching and login are blocked while impersonation is active (redirect to the target tenant home
  or the stop page). This prevents `SignInAsync` from overwriting the actor cookie with a different
  principal and losing the durable actor pointer.

## 8. UI

### 8.1 Start page

Route `/platform-admin/user-impersonation`, Razor page
`Pages/PlatformAdmin/UserImpersonation/Index.cshtml(.cs)`.

- Page header: "User Impersonation", `ph ph-user-switch` or `ph ph-mask-happy`.
- Warning alert: "You will act as the selected user. Every action is audited. Sessions are short-lived."
- Tenant selector (active tenants first) and user search (`#user-search`, `name="q"`).
- Results table `table.data-table` with columns User, Email, Status, Roles (or Tenant admin badge), and a
  radio/button to select.
- Form fields: selected `targetUserId`, `reason` (required, max 500), `ticketReference`, `expiryMinutes`
  (default 15, 1–60), optional `returnUrl`.
- Start button uses caution styling (`btn-danger`/`btn-warning`), label "Start impersonation".
- If an active impersonation exists, show a danger card with target, actor, remaining time, and an end form.
- Link to history and to Support Access with a short comparison note.
- Stable test ids: `data-testid="impersonation-start-form"`, `data-testid="impersonation-user-row"`,
  `data-testid="impersonation-start-button"`.

### 8.2 Banner

`Pages/Shared/_UserImpersonationBanner.cshtml`, included in `_Layout.cshtml` after the support/delegated
banners.

- `alert alert-danger`, `role="alert"`, warning icon.
- Text: "IMPERSONATION MODE — Acting as {target display/name} ({target username}) in {tenant}."
- Actor line: "Platform administrator: {actor username}. Reason: {reason}. Ticket: {ticket}. Expires in
  {remaining}."
- POST form to `/impersonation/stop` with button "End impersonation".
- `data-testid="user-impersonation-banner"`, `data-testid="end-impersonation-button"`.
- The banner must render on tenant pages, account pages, platform-admin pages, and error pages when a session
  is active. It must not render when the row is ended/expired.

### 8.3 Stop page

Route `/impersonation/stop`, `Pages/Impersonation/Stop.cshtml.cs`.

- `POST` only (GET redirects to the start page).
- `[Authorize(Policy = "platform-admin")]`; overlay excluded for `/impersonation`.
- Load the row by session key, verify the actor, end it, clear the key and preferred tenant, redirect.
- Anti-forgery is required.

### 8.4 History

Route `/platform-admin/user-impersonation/history`,
`Pages/PlatformAdmin/UserImpersonation/History.cshtml(.cs)`.

- `[Authorize(Policy = "platform-admin")]`.
- Table `table.data-table`: Actor, Target user, Tenant, Reason, Ticket, Started, Ended, Duration, Status.
- Filters: actor username, target username, tenant slug, start/end date. Pagination (50/page).
- Active rows highlighted. Empty state.
- No CSV requirement for the first iteration; mirror the support history layout.

### 8.5 Navigation and redirects

- Add "User Impersonation" and "Impersonation History" links to the Platform Administration sidebar in
  `_Layout.cshtml`.
- Change legacy redirects in `EndpointMappingExtensions`:
  - `/platform-admin/impersonation` → `/platform-admin/user-impersonation`
  - `/platform-admin/impersonation-history` → `/platform-admin/user-impersonation/history`
- Update `e2e/instructions/platform-admin-impersonation.md` and
  `platform-admin-impersonation-history.md` to describe the new user-level pages (or replace them with
  `platform-admin-user-impersonation*.md` used by the new routes).

## 9. Demo applications

### 9.1 RazorClient (`Examples/MrWhoOidc.RazorClient`)

- `_Layout.cshtml`: if the authenticated principal has `impersonation_id` or
  `urn:mrwho:impersonation-session-id`, render a red banner: "Impersonation session — this application is
  seeing the impersonated user's identity. Actor: {act sub}." Use `data-testid="razorclient-impersonation-banner"`.
- `Pages/Index.cshtml`: the existing claims table naturally shows `sub`, `name`, `act`, `impersonation_id`.
  Add a small "Impersonation" section that explicitly shows target `sub` and actor `act.sub` when present.
- No app code changes are needed for the identity swap itself: the app consumes the ID token claims.

### 9.2 OidcDemo (`Examples/MrWhoOidc.OidcDemo`)

- `_Layout.cshtml`: same banner based on claims.
- `Pages/Index.cshtml`: add explicit Actor/Impersonation rows above the raw claims table when the claims are
  present.

### 9.3 TestApi (`Examples/MrWhoOidc.TestApi`)

- Extend `GET /me` with `impersonationId` (from `impersonation_id`) and `actor` (from `act.sub`) when present.
- Keep existing `actorClient`/delegation behavior unchanged.

### 9.4 React client

Optional for this iteration. The generic claims table already shows the new claims; add a banner if time
permits.

## 10. Tests

### 10.1 Unit tests

- Start rejects: non-platform-admin, inactive tenant, inactive target, self-impersonation, active support
  session, active impersonation, blank reason, out-of-range duration clamped.
- Start creates target OP session and durable row; session key set; audit emitted.
- Stop marks `Ended`, revokes target OP session and only its refresh tokens, clears key.
- Expiry cleanup marks `Expired` and revokes.
- Overlay builds target principal with target `sub`, target `sid`, actor custom claims, and target stamp.
- Overlay rejects actor mismatch, changed security stamp, deactivated target, revoked target session.
- Cookie validator permits an actor cookie on the target tenant while impersonation is active, and still
  rejects an expired/revoked admin session.
- `EffectiveAccessContextAccessor` returns `UserImpersonation` with actor/subject distinct.
- Token exchanger emits `act` and `impersonation_id` for a code whose `sid` matches an active impersonation.

### 10.2 E2E (Playwright)

Add `e2e/tests/test_user_impersonation.py` (or a class in `test_platform_admin_pages.py`):

1. **Start page**: platform admin opens `/platform-admin/user-impersonation`, sees the heading, warning,
   tenant selector and user search. Search `alice`, select her, enter reason, start.
2. **Banner and account identity**: after redirect, `data-testid="user-impersonation-banner"` is visible and
   names Alice, the actor, tenant and remaining time. Navigate to `/account` (or the target tenant account
   page); the profile shows Alice's username/name, not the admin's.
3. **OIDC demo identity**: clear only the RazorClient app cookie (default `.AspNetCore.Cookies`) and visit
   `https://localhost:5003`; sign in. The app's claims table shows Alice's `name`, a target `sub` different
   from the admin's, and `impersonation_id`/`act`. The RazorClient banner is visible.
4. **Stop**: click End impersonation; banner disappears, `/platform-admin/user-impersonation` shows no active
   session, `/account` shows the admin again. RazorClient refresh tokens issued during the session are
   revoked (verify by refreshing or by a DB/API assertion where practical).
5. **History**: the history page shows the actor, target, tenant, reason, status and duration.
6. **Legacy redirects**: `/platform-admin/impersonation` redirects to the new page;
   `/platform-admin/impersonation-history` redirects to the new history.
7. **Validation**: blank reason fails; selecting an inactive user/tenant fails; a non-platform-admin cannot
   load the page or post to start/stop.

E2E must clean up any active impersonation in a `finally` block, just as support-access tests do, so a failed
assertion does not contaminate later tests. Example-app cookies set during the test must be cleared or the
test must run with a dedicated cookie name/context.

## 11. Documentation and rollout

- Add `docs/reference/user-impersonation.md` describing the feature, the actor/subject model, the security
  invariants, the `act`/`impersonation_id` claims, and the difference from Support Access.
- Update `docs/reference/delegated-and-support-access.md` with a link and a short comparison table.
- Update `docs/admin-guide.md` and `docs/production-setup-guide.md` (impersonation is disabled by default;
  enable `Auth:EnableUserImpersonation`; audit/retention guidance).
- Update `docs/example-applications-guide.md` for the demo banner/claims.
- Keep the feature flag off by default in production. Local dev and E2E enable it explicitly.
- The legacy `ImpersonationAuditLogs` table is not used by the new feature. Mark it obsolete in docs and
  remove it in a later cleanup migration.

## 12. Decisions

1. **User-scoped, not tenant-scoped.** The target is one `User`/`UserAccount`, not a tenant admin role.
2. **Overlay principal, not cookie replacement.** The actor's auth cookie remains; middleware installs the
   target principal for the request. This avoids losing the actor and avoids a stolen target session being
   able to restore an admin cookie.
3. **Target OP session.** A separate OP session gives correct token `sid`/logout semantics and lets stop
   revoke exactly the refresh tokens issued while impersonating.
4. **Writable.** The feature assumes the target's permissions, including writes. The banner and audit make
   the elevated action visible; Support Access remains the read-only option.
5. **Platform-admin surfaces are not impersonated.** `/platform-admin` always runs as the actor. This keeps
   platform authority out of the target identity.
6. **Feature flag default off.** High-risk feature; local/E2E enable it explicitly.
7. **No reuse of the legacy session-only implementation or its audit table.**
