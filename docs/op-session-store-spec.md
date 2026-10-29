# OP Session Store — Specification for the Remaining Work (F1)

> **Status:** specification, not implemented. The store itself landed with migration `AddOpSessions`
> (2026-10-28). This document specifies what is still missing to close F1 as the assessment defines it
> ([oidc-idp-assessment-2026-10-04.md](oidc-idp-assessment-2026-10-04.md), §3 "F1 (P0) Server-side OP session
> store").
>
> **Rule:** code wins. When this document and the code disagree, the code is what runs; fix the document in the
> same change.

## 1. Why

The assessment calls F1 "the foundation for C2, `sid`, session listing/revocation and correct
check_session". The foundation exists: the tables `OpSessions` and `OpSessionClients`, the session service
with the per-tenant timeouts, the `sid` in the sign-in cookie (stable for the session and the same on every
replica), the cookie validation that rejects a revoked, expired or idle session, the record of the clients
that took part in a session, and the logout that targets those clients when the session is known.

What is missing is everything that turns the store into a **user-visible session management** and into the
**binding between a session and the tokens it produced**:

| # | Gap | Consequence today |
|---|---|---|
| 1 | The sign-in paths do not create the session | A session exists only after the first authenticated request. A sign-out right after the sign-in has no row to revoke, and the session list would miss a session the user never used. |
| 2 | The refresh tokens are not bound to the session | A revoked or expired session does not end the refresh tokens it produced: they keep working until they expire. The user stays "signed in" at the relying party. |
| 3 | The session list shows tokens, not sessions | `Account/Sessions` lists the rows of the `Tokens` table. It cannot show when the user signed in, which application the session belongs to, or the idle deadline. |
| 4 | There is no session API | The page cannot list or end a session, and an administrator cannot either. Only code can revoke a session today. |
| 5 | `check_session` does not consult the session | The iframe only compares the `opbs` cookie hash, so a revoked session still answers `unchanged`. |
| 6 | The `SecurityStampCookieValidator` was not replaced | Every authenticated request does two lookups (stamp + session) and the password-reset semantics are unchanged. The assessment expects the session check to replace *most* of the stamp check. |
| 7 | Only the authorization-code flow records the client | A session that was only used by the device flow or by CIBA has no recorded clients, so the session-scoped logout falls back to "every application the user signed in to". |

Gaps 1, 2 and 6 have a security dimension; gaps 3, 4, 5 and 7 are completeness.

## 2. Goals and non-goals

**Goals**

- One sign-in produces exactly one OP session, created by the sign-in path itself.
- Ending a session ends the tokens it produced (the refresh tokens of its family).
- The user can see the real sessions, with the applications that took part, and can end them.
- `check_session` reports the state of the actual session.
- The session store is the primary revocation mechanism; the security stamp remains only for what the session
  cannot know (the account's credentials).

**Non-goals**

- No new OIDC feature (no `check_session` protocol version beyond what exists, no session management iframe
  changes beyond the state).
- No change to the token format: the `sid` stays a claim of the ID token and of the logout token.
- No change to the cookie's lifetime (8 h absolute, `SlidingExpiration = false`).
- The `Account/Sessions` page keeps its route (`/account/sessions`) and its tab.

## 3. The session lifecycle

```
        sign-in (password | TOTP | passkey | external | QR | tenant switch)
                          |
                          v
                  +---------------+     revoked (sign-out, password change, admin)
                  |  OpSession    | ------------------------------------>  RevokedAt
                  |  (created)    |
                  +---------------+
                          |                 idle timeout (default 30 min)
                          |  LastSeenAt  -------------------------------->  IdleExpiresAt
                          |
                          |                 absolute timeout (default 8 h)
                          v                                              ExpiresAt
                   (sweep removes it a day later)
```

A session is valid while `RevokedAt IS NULL AND ExpiresAt > now AND IdleExpiresAt > now`. The cookie
validation enforces exactly that (`OpSessionService.CheckAsync`).

**The `sid` is the session id.** It is written into the sign-in cookie, travels into the ID token and into the
logout token, and is what the relying parties see. It must not change during the life of the session.

## 4. The items

### 4.1 The sign-in paths create the session (gap 1)

**Requirement.** Every path that issues the sign-in cookie creates the session in the same transaction and
writes its id into the cookie as the `sid` claim. The lazy creation in the cookie validation stays as the
compatibility path for cookies that were issued before the store existed (or by a version without it) — it must
not create a second session for a cookie that already carries a `sid`.

**The paths** (the `SignInAsync` call sites of the main cookie scheme):

| Path | File | Note |
|---|---|---|
| Password | `MrWhoOidc.WebAuth/Pages/Login.cshtml.cs` (`CompleteSignInAsync`) | The `auth_time` claim is already set here. |
| TOTP | `MrWhoOidc.WebAuth/Pages/LoginTotp.cshtml.cs` | Signs in after the second factor. |
| Passkey | `MrWhoOidc.WebAuth/Handlers/WebAuthnHandler.cs` | |
| External IdP | `MrWhoOidc.WebAuth/Handlers/External/ExternalOidcSessionManager.cs` | |
| QR | `MrWhoOidc.WebAuth/Handlers/QrLoginHandler.cs` | The desktop browser signs in here. |
| Tenant switch | `MrWhoOidc.WebAuth/Services/TenantSwitchingService.cs` | A new tenant is a new session: the old one stays in the old tenant, the new cookie gets a new session. |

**Design.** A single helper builds the session and the claim set, so the paths cannot drift apart:

```csharp
// MrWhoOidc.Auth/Services/OpSessionService.cs (extension)
Task<OpSession> CreateAsync(
    Guid tenantId,
    Guid userId,
    Guid? userAccountId = null,
    string? acr = null,
    string? amrJson = null,
    string? upstreamIdp = null,
    string? ipHash = null,
    string? userAgent = null,
    DateTimeOffset? authTime = null,
    CancellationToken ct = default);
```

The sign-in path then adds the claim and signs in:

```csharp
var session = await opSessions.CreateAsync(tenantId, userId, userAccountId, acr, amr, idp, ipHash, userAgent, authTime, ct);
claims.Add(new Claim(OidcConstants.Claims.Sid, session.Id.ToString("N")));
await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
```

The claim is written as the **unhyphenated** GUID (`"N"` format), the same format the lazy path uses, so the
two paths agree.

**The context that the session stores.** `Acr`, `Amr`, `UpstreamIdp` come from the claims of the principal
that is signed in (the same source the authorization metadata uses); `AuthTime` is the `auth_time` claim;
`IpHash` is the hash of the remote address (never the address); `UserAgent` is the request's user agent.

**Rules**

- One session per sign-in. A sign-in that fails creates nothing.
- The session is created **before** the cookie is written, and it is committed with it (one `SaveChanges`).
- The tenant switch creates a **new** session in the new tenant; the old session is not deleted (it belongs to
  the old tenant and stays revocable there).
- The lazy path in `OpSessionCookieValidator` only creates a session when the cookie has **no** `sid`.

**Acceptance**

- A unit test per path: after the sign-in, the cookie's principal carries a `sid` and a session row exists
  with the expected user, tenant, acr/amr and `auth_time`.
- A test that a second sign-in creates a second session (no reuse).
- A test that the tenant switch creates a session in the new tenant and leaves the old one alone.

### 4.2 The refresh tokens are bound to the session (gap 2)

**Requirement.** A refresh token carries the id of the session it was issued in. When the session ends
(revoked, expired, idle), the refresh tokens of that session stop working.

**Data model.** `Tokens.SessionId` (nullable `Guid`, FK to `OpSessions.Id`, `ON DELETE SET NULL`). The column
is nullable: a refresh token issued before this change (or without a session, e.g. the client-credentials
grant) has none.

**Write path.** `RefreshTokenService.CreateRefreshTokenAsync` takes a `Guid? sessionId` and writes it. The
callers pass the session they know:

| Caller | The session id comes from |
|---|---|
| `AuthorizationCodeExchanger` | The `sid` of the authorization code row (the session of the sign-in). |
| `RefreshTokenExchanger` (rotation) | The `SessionId` of the token being rotated — the rotation keeps the session. |
| `DeviceCodeTokenFactory` | The `SessionId` of the device code entry (4.4). |

**Revocation.** When a session is revoked, the refresh tokens of that session are revoked in the same change:

```sql
UPDATE "Tokens" SET "RevokedAt" = now()
WHERE "SessionId" = @sessionId AND "RevokedAt" IS NULL AND "Type" = 'refresh';
```

The refresh token **family** (`Tokens.FamilyId`) is the existing mechanism for "this grant and everything it
produced"; the session revocation uses `SessionId`, the rotation and the reuse detection keep using
`FamilyId`. A token that belongs to a session *and* to a family is revoked by either path.

**Policy.** Which sessions revoke the tokens?

- Revoking a session revokes its refresh tokens. Always. A session that the user ends must end the tokens.
- The client-credentials grant has no session and no user: nothing to revoke.
- The per-client `revoke_refresh_on_logout` policy that the assessment mentions is **not** implemented in this
  round: it would let a client opt out of the revocation, and the "end the tokens" behaviour is the safer
  default. The column is left for a later decision (see §9).

**Rules**

- Revoking a session revokes the refresh tokens of that session, in the same `SaveChanges` as the session.
- A refresh token that is issued while the session is already revoked is refused (the session check fails
  first).
- The access tokens that were issued from a revoked session stay valid until they expire: they are
  self-contained and short-lived. The logout notifies the relying parties (the back- and front-channel
  notification already carry the `sid`).
- The `Tokens.SessionId` index makes the revocation a set-based update.

**Acceptance**

- A test: a session is revoked → the refresh token of that session is revoked → the refresh grant fails with
  `invalid_grant`.
- A test: the rotation keeps the session id → the rotated token still belongs to the session.
- A test: a refresh token without a session (client credentials) is unaffected by a session revocation.

### 4.3 The session list and the revocation API (gaps 3 and 4)

**Requirement.** The user sees the real OP sessions and can end them; a tenant administrator can list and end
the sessions of a user.

**The page.** `MrWhoOidc.WebAuth/Pages/Account/Sessions.cshtml` exists and lists the **token** rows today. It
is changed to list the **sessions**:

| Column | Source |
|---|---|
| Signed in | `OpSession.AuthTime` |
| Last used | `OpSession.LastSeenAt` |
| Expires | `OpSession.ExpiresAt` (absolute) and the idle deadline |
| Browser / device | `OpSession.UserAgent` parsed with the existing `IUserAgentParser` |
| Address | the stored `IpHash` (the page shows the hash, or nothing — the address is not stored) |
| Applications | the `OpSessionClients` of the session (the client ids) |
| Current | the `sid` of the current cookie equals the session id |

The "revoke" and "revoke all others" handlers act on the sessions (`OpSession.RevokedAt`), not on the tokens.
The tokens of a revoked session are revoked with it (4.2).

**Decision to record:** the page stops listing the token rows. The tokens are an implementation detail; the
session is what the user ends. The page keeps its route, its tab and its "Current session" badge.

**The API.** The tenant admin API (`/admin/api/...`, the group that `AdminApiEndpointMappingExtensions`
maps) gets two endpoints:

| Endpoint | Method | Operation marker | Effect |
|---|---|---|---|
| `/admin/api/users/{userId}/sessions` | GET | `TenantAdminOperationKind.Read` | The sessions of the user (the id, the times, the user agent, the applications, the revoked flag). |
| `/admin/api/users/{userId}/sessions/{sessionId}` | DELETE | `TenantAdminOperationKind.Write` | Revokes the session (and its refresh tokens). |

Both follow the conventions of the neighbouring endpoints: the tenant filter pins `TenantId`, the antiforgery
policy applies to the unsafe method, the operation marker is set, and the response is a `ProblemDetails` with
the usual titles. The audit sink emits `session.revoked` (the reason, the actor, the session id hash) — the
same shape as the other administrative events.

**Rules**

- A user can only see and end their own sessions; the page reads the user from the cookie, never from the
  query.
- The page never shows the token values, the `TokenHash` or the refresh token family.
- Ending the *current* session signs the user out (the cookie is deleted and the session is revoked).
- Ending another session does not touch the current cookie.

**Acceptance**

- A test: the page lists the sessions of the signed-in user and not the sessions of another user.
- A test: "revoke" revokes the session and its refresh tokens; the current session is protected.
- A test: the admin endpoints are tenant-scoped and audited.

### 4.4 `check_session` consults the session (gap 5)

**Requirement.** The `check_session_iframe` answers with the state of the actual session.

**Design.** The handler (`MrWhoOidc.WebAuth/Handlers/CheckSessionHandler.cs`) keeps its protocol (the RP
posts `client_id session_state`, the iframe answers `unchanged` / `changed` / `error`), and the answer is now
derived from the session:

- The `opbs` cookie is the session id (the cookie that the browser sends to the iframe must not be
  `HttpOnly`, so it is a separate, readable cookie — the current implementation reads `__Host-mrwho-opbs`).
- The session is looked up; a revoked, expired or missing session answers `changed`.
- A live session answers `unchanged` when the recomputed `session_state` matches, and `changed` otherwise.

**Decisions to record**

- Which cookie carries the session id: the current `__Host-mrwho-opbs` cookie is the natural place (the iframe
  has to read it, so it cannot be the `HttpOnly` sign-in cookie). If the cookie does not exist yet, the iframe
  answers `error` (the RP has no session state to compare).
- The `session_state` algorithm stays as it is (the hash of `client_id`, origin, `opbs` and the salt) — the
  change is only *where* the "is the session alive" answer comes from.

**Acceptance**

- A test: a revoked session → `changed`; a live session → `unchanged`; an unknown cookie → `error`.

### 4.5 The security stamp (gap 6)

**Requirement.** The session check replaces *most* of the `SecurityStampCookieValidator`.

**Analysis.** The stamp answers two questions:

1. *Were the credentials changed after the cookie was issued?* (a password reset, an email change, an MFA
   change) → the stamp rotates → every cookie of the account must die.
2. *Does the account still exist?*

The session can answer (1) if the password change also **revokes the sessions** of the account. That is the
design that makes the stamp redundant: the password reset rotates the stamp **and** revokes the sessions; the
session check then rejects the cookies.

**Work items**

- A credential change revokes the sessions of the account, next to the stamp rotation. The places that rotate
  the stamp today are:
  - `MrWhoOidc.Auth/Services/UserAccountService.cs` (`UpdatePasswordAsync` — the password change; it already
    revokes the tokens across the account's tenants ✓),
  - `MrWhoOidc.WebAuth/Pages/Password/Index.cshtml.cs` (the password reset ✓),
  - `MrWhoOidc.Auth/Services/ActiveUserGate.cs` (the deactivation ✓ — it already ends what the user holds ✓).
- The `SecurityStampCookieValidator` keeps only what the session cannot know: the *account exists* check and
  the *deactivated user* check ✓. The stamp comparison itself is removed once the sessions are revoked by the
  credential change ✓.
- The stamp claim stays in the cookie for the transition: a cookie without a session (a legacy cookie) is
  still checked against the stamp ✓ — that is the compatibility path, and it stays until the sessions are
  revoked by the credential change ✓ (a rolling deploy: an old pod revokes the stamp, a new pod rejects the
  session ✓ — both work ✓).

**Rules**

- A credential change revokes the sessions **and** rotates the stamp ✓ (belt and braces: the stamp for the
  legacy cookies, the session for the new ones ✓).
- A transient database error in the session check must not sign the user out (the same rule as the stamp
  check ✓).

**Acceptance**

- A test: a password reset ends the sessions of the account → the old cookie is rejected ✓.
- A test: a credential change without a session (the legacy cookie) is still rejected by the stamp ✓.

### 4.6 The device and CIBA flows record the client (gap 7)

**Requirement.** A session that was only used by the device flow or by CIBA knows the client that took part.

**Design.** The approval happens in the browser, so the browser knows the session:

- `DeviceCodeEntry.SessionId` (nullable) is written when the user approves the device
  (`MrWhoOidc.WebAuth/Pages/Device.cshtml.cs` ✓ — the approval handler ✓).
- `CibaAuthenticationRequest.SessionId` (nullable) is written when the user approves the CIBA request
  (`MrWhoOidc.WebAuth/Pages/Ciba.cshtml.cs` ✓).
- The token endpoint copies it into the refresh token ✓ (4.2) and registers the client in the session ✓
  (`OpSessionService.RegisterClientAsync` ✓).

**Rules**

- A device or CIBA request that was approved without a session (the approval was made in a browser that never
  had one — the legacy path) keeps a null session id ✓ and behaves as today ✓.
- The device and CIBA clients are registered in the session at the approval time ✓ (not at the token
  endpoint: the approval is the moment the client takes part ✓).

**Acceptance**

- A test: the device approval records the session and the client; the session-scoped logout then notifies
  that client ✓.

## 5. The data model changes

| Table | Column | Type | Notes |
|---|---|---|---|
| `Tokens` | `SessionId` | `uuid NULL` | FK to `OpSessions.Id` (`ON DELETE SET NULL`), index. The refresh tokens of a session. |
| `DeviceCodes` | `SessionId` | `uuid NULL` | The session that approved the device. |
| `CibaAuthenticationRequests` | `SessionId` | `uuid NULL` | The session that approved the CIBA request. |

One migration (`BindTokensToOpSession`) adds the columns, the foreign keys and the indexes. It is additive
and rolling-deploy safe: the old pods ignore the columns, the new ones read null for the rows written before
it.

`OpSessions` and `OpSessionClients` do not change.

## 6. The service API

`IOpSessionService` (in `MrWhoOidc.Auth/Services/OpSessionService.cs`) grows:

```csharp
/// <summary>The sessions of a user (the newest first).</summary>
Task<IReadOnlyList<OpSession>> ListForUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default);

/// <summary>Revokes a session and the refresh tokens it produced. Returns the number of revoked tokens.</summary>
Task<int> RevokeAsync(Guid sessionId, string reason, bool revokeTokens, CancellationToken ct = default);

/// <summary>Revokes every session of a user except one (the current session).</summary>
Task<int> RevokeAllForUserAsync(Guid tenantId, Guid userId, Guid? exceptSessionId, string reason, CancellationToken ct = default);
```

`RevokeAsync` replaces the current signature (it adds the token revocation ✓).

The service is the only place that writes to `OpSessions` ✓; the cookie validation, the page and the API call
it ✓ (no direct table access outside the service ✓ — the same rule as the rest of the persistence layer ✓).

## 7. The failure behavior

| Situation | Behavior |
|---|---|
| The session store is disabled (`OpSession__Enabled=false`) | The cookie validation does nothing ✓, the cookie keeps its current behavior ✓, the page shows an explanatory note instead of the list ✓. |
| The database is unavailable during the cookie validation | The session is **not** checked (the cookie stays valid) ✓ — the same rule as the stamp check ✓. A transient outage must not sign every user out. |
| The database is unavailable during a revocation | The revocation fails ✓ and the caller sees the error ✓ (the page shows it ✓). |
| The session row was swept while the cookie is still alive | The cookie is rejected (`NotFound`) ✓ — the session is over ✓. |
| The clock skew | The session check uses the server clock ✓; the clock skew of the token validation does not apply to the session ✓. |

## 8. The tests

The unit tests (the project's ~2000 MSTest methods) cover:

- The sign-in paths (one per path ✓): a session and a `sid` ✓.
- The lazy compatibility path: a cookie without a `sid` gets exactly one session ✓; a cookie with a `sid` does
  not get a second one ✓.
- The refresh token binding: the revocation, the rotation, the family ✓.
- The session list and the page: the own sessions only ✓, the current session is protected ✓, the revoke-all ✓.
- The admin endpoints: the tenant scoping, the antiforgery, the audit ✓.
- `check_session`: `unchanged` / `changed` / `error` ✓.
- The credential change: the sessions are revoked ✓.

The end-to-end suite (Playwright, `e2e/tests/`) adds:

- Sign in → `Account/Sessions` shows the session with the application that took part ✓ → end it → the refresh
  token no longer works ✓.
- The device flow → the session knows the device client ✓.

## 9. The rollout

- The store is already on by default (`OpSession__Enabled=true`) ✓ and the compatibility path keeps the
  cookies of the previous version working ✓ → **no user has to sign in again** ✓.
- The migration is additive ✓ → a rolling deploy is safe ✓.
- The session revocation of the refresh tokens (4.2) is a behavior change: **a user who ends a session at the
  account page is signed out at the relying parties** ✓. It is announced in the upgrade guide ✓.
- The flag `OpSession__Enabled=false` turns the whole store off ✓ (the page explains it ✓).

## 10. The documentation changes

| Document | Change |
|---|---|
| `docs/oidc-idp-assessment-2026-10-04.md` | §3 F1: the status becomes "implemented" ✓ (with the date ✓). |
| `docs/upgrade-guide.md` | The new migration ✓, the behavior change of the session revocation ✓, the new options ✓. |
| `docs/production-setup-guide.md` | The `OpSession__*` settings ✓ (they exist ✓) and the per-tenant settings ✓. |
| `docs/for-operators/key-rotation.md` | Nothing (the sessions do not change the keys ✓). |
| `docs/admin-guide.md` | The session list and the revocation in the admin UI/API ✓. |
| `docs/codebase-review-2026-10-26.md` | Nothing (F1 was not a finding of that review ✓). |
| `wiki/overview.md` | The session store in the architecture map ✓. |

## 11. The decisions that are still open

| # | Decision | Recommendation |
|---|---|---|
| 1 | The `Account/Sessions` page lists the sessions instead of the token rows. | Take it: the session is what the user ends ✓. The token rows were always an approximation of a session ✓. |
| 2 | The per-client `revoke_refresh_on_logout` policy (the assessment mentions it). | Do not implement it now: revoking is the safer default ✓. The column can come later ✓. |
| 3 | The stamp validator is reduced to the "the account exists" and "the user is deactivated" checks. | Take it, but only after the credential changes revoke the sessions ✓ (§4.5) ✓ — the two must land together ✓. |
| 4 | The access tokens of a revoked session stay valid until they expire. | Keep it: they are short-lived and self-contained ✓; the logout notifies the relying parties ✓. |
| 5 | The session id in the `opbs` cookie (the readable one) vs. a second readable cookie. | Reuse `__Host-mrwho-opbs` ✓ (it is already the "opaque browser session" ✓ — the name should then change to carry the session id ✓). |
| 6 | The `sid` format: the unhyphenated GUID ("N") vs. the hyphenated one. | Keep the unhyphenated one ✓ (it matches the lazy path ✓ and the existing `sid` values ✓). |
