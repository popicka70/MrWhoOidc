# ADR-0010: Dedicated Audience and Scope for Admin API Bearer Tokens

**Status**: Proposed  
**Date**: 2026-10-04  
**Decision Makers**: Engineering Team  
**Related**: [OIDC IdP assessment §2.4 R1, §2.5 H3](../oidc-idp-assessment-2026-10-04.md), [ADR-0001: Token Format](./001-token-format.md)

---

## Context

### Problem statement

**Any access token issued to any relying party can call the admin APIs (H3).** The admin bearer scheme (`api-bearer`, `ApiTokenAuthHandler`) checks only these things:

- the signature, against the current tenant's keys;
- the lifetime;
- revocation;
- `aud ∈ AuthOptions.ApiAudiences`, which defaults to `["api"]`.

Ordinary RP tokens carry the default audience whenever the RP sends no `resource`:

| Grant | Default `aud` | Where |
|---|---|---|
| authorization_code | the single `ApiAudiences` entry | `AuthorizationCodeExchanger.cs:162-180,705-713` |
| refresh_token | `ApiAudiences.FirstOrDefault() ?? "api"` | `RefreshTokenExchanger.cs:90-92` |
| device_code | first `ApiAudiences` entry, otherwise `"api"` | `DeviceCodeGrantHandler.cs:155-157` |
| CIBA | `entry.Resource ?? "api"`, hard-coded | `CibaGrantHandler.cs:164` |

None of the three admin authorization handlers looks at `scope`, `client_id`/`azp`, `aud` or `typ`:

- `TenantAdminAuthorizationHandler`
- `PlatformAdminAuthorizationHandler`
- `AdminAuthorizationHandler`

They check only role assignments in the database. So when a tenant or platform admin signs in to *any* RP in the tenant, that RP (or anyone who steals its token) can call the following with the admin's privileges:

- `/t/{slug}/admin/api/**`
- `/platform-admin/api/**`
- export, import and audit endpoints
- `/api/webauthn/*`

That includes an RP registered through anonymous DCR.

Two further weaknesses in the same handler:

1. **The issuer is never really checked.** `TokenValidator` sets `ValidIssuer` to the token's own unverified `iss` (`TokenValidator.cs:64-87`). On `/t/{slug}/...` routes the tenant comes from the path, and `iss` is never compared with that tenant's `IssuerUri` (`ApiTokenAuthHandler.cs:49,59-66`).
2. **The token type is not checked.** There is no `typ: at+jwt` check, so an ID token whose `aud` happens to match would be accepted.

**R1 (MrWhoOidc.ApiService) is the same problem in a legacy service.** That service:

- reads the tenant from the `X-Tenant-Id` / `X-Tenant-Slug` headers ahead of the token's claims (`Program.cs:149-169`);
- runs with `ValidateAudience = false`;
- authorizes from the token's `realm`/`roles` claims instead of the database.

It appears only in the Aspire AppHost (`AppHost.cs:9-12`) and the solution file. It is not in either docker-compose file, and nothing calls its `/admin/...` routes. The CLI calls WebAuth's `/admin/api/...`.

### Who calls the admin APIs today

| Caller | Auth | Affected by this ADR |
|---|---|---|
| Razor admin pages (`/Admin`, `/PlatformAdmin`) | cookie | No |
| e2e Playwright tests (`e2e/conftest.py:529-795`) | cookie (saved storage state) | No |
| **MrWhoOidc.Cli**, including MCP (see below) | Bearer | **Yes** |
| e2e CLI tests (`e2e/tests/test_cli_operations.py`) | Bearer via the CLI | Yes, through the CLI |
| Examples (GoApi, RazorClient, TestApi, …) | — | No: they never call admin APIs. They rely on `aud=api` for their own APIs, which stays as it is. |

How the CLI calls the admin APIs:

- It gets a token through the **device code** grant (`LoginCommand.cs:10,78,98`).
- It uses the system client `mrwho-cli-{slug}`. That client is public (`none`), and is created only when an admin enables CLI access (`CliClientService.cs:28-41,44-145`).
- It requests `openid profile email roles tenants offline_access` and sends no `resource`.
- It sends `Authorization: Bearer` to `/t/{slug}/admin/api/...` and `/platform-admin/api/...` (`CliServerConnection.cs:139-143,230-255`).

No admin scope exists today. "admin" appears only as a realm/role name.

---

## Decision

Admin API bearer tokens become a **separate kind of token**:

- they are requested explicitly;
- only designated admin clients can obtain them;
- they are useless at ordinary RPs.

Ordinary RP tokens become useless at the admin API.

### 1. A dedicated resource and scope

- **Resource:** `urn:mrwho:admin-api` (RFC 8707). It is **not** added to `ApiAudiences`, so no grant ever defaults to it.
- **Scope:** `mrwho:admin`. It is a system scope that admins can't assign to ordinary clients.

### 2. Issuance is gated per client

A client may obtain `aud=urn:mrwho:admin-api` and `mrwho:admin` only when **both** of these hold:

- `Client.IsSystemClient` is true;
- the client has the new `AllowAdminApi` flag. It defaults to `false`, and the flag can be changed only by platform admins; the `PlatformRealmWriteGuard` pattern can extend to cover it.

The checks sit in the existing places:

- **resource:** `ResourceIndicatorPolicy.IsAllowed`, which already covers authorize, code, refresh, device and CIBA after C9/H6;
- **scope:** scope validation at device authorization and `/authorize`.

**The token is never a default.** The client must send `resource=urn:mrwho:admin-api` and `scope=… mrwho:admin`. Refresh keeps the resource and scope it was granted and cannot widen them.

### 3. `api-bearer` validation

The admin bearer scheme requires **all** of the following:

| Check | Rule |
|---|---|
| `typ` header | `at+jwt` (RFC 9068) |
| `iss` | equals the resolved tenant's `IssuerUri`. Platform routes use the default tenant's. The token's own `iss` is no longer trusted as the expected value. |
| `aud` | contains `urn:mrwho:admin-api` |
| `scope` | contains `mrwho:admin` |
| `client_id` | a client with `AllowAdminApi` in the token's tenant |
| lifetime, revocation, signature | unchanged |

The role checks in the database stay exactly as they are; this ADR does not change authorization. The new checks run in the authentication handler, so a failure is a `401` with `WWW-Authenticate: Bearer error="invalid_token"`, not a `403`.

`/api/webauthn/*` currently accepts any bearer token through the default scheme. It moves to cookie-only, because passkey management is a browser activity.

### 4. CLI changes

- Device authorization sends `resource=urn:mrwho:admin-api` and adds `mrwho:admin` to `DefaultScope`.
- `CliClientService.EnableCliAccessAsync` sets `AllowAdminApi = true` on the CLI client and assigns the `mrwho:admin` scope.
- A migration does the same for existing `mrwho-cli-*` clients.
- The CLI keeps a separate session (refresh token) for admin use. It does not reuse an RP token.

### 5. R1: retire MrWhoOidc.ApiService

The recommendation is to **delete** `MrWhoOidc.ApiService`, its AppHost registration and its solution entry. Every endpoint it exposes exists in WebAuth's `/admin/api` with database-backed authorization. If it must stay, it adopts §3: the tenant comes only from `iss`, the audience is validated, and the headers are ignored.

---

## Rollout

| Phase | Change | Compatibility |
|---|---|---|
| **1** (one release) | Server: resource, scope, `AllowAdminApi` flag, migration for CLI clients, stricter `iss`/`typ` checks. CLI: request the admin resource. `api-bearer` accepts **both** new admin tokens and legacy `aud=api` tokens, behind `AdminApi:AcceptLegacyTokens` (default `true`). Legacy acceptance logs a warning and increments `mrwho_admin_api_legacy_token_total{client_id}`. | Old CLIs keep working; operators can see who still uses legacy tokens. |
| **2** (next release) | `AdminApi:AcceptLegacyTokens` defaults to `false`. Remove ApiService. | Old CLIs must upgrade: `mrwho login` again. |
| **3** | Remove the flag and the legacy code path. | — |

During phase 1 the stricter `iss` and `typ` checks apply to legacy tokens as well. They close the cross-tenant and ID-token gaps right away, independently of the audience change.

---

## Consequences

**Positive**

- RP tokens can no longer reach the admin APIs, whatever roles the user has (closes H3).
- CLI admin tokens are useless at RPs, because no RP accepts `urn:mrwho:admin-api`.
- Every admin API call can be attributed to a client (`client_id`) as well as a user, which helps NIS2/DORA audit.
- The fix uses existing machinery (RFC 8707, `ResourceIndicatorPolicy`, scopes) and adds no new key or issuer.

**Negative / costs**

- CLI users must upgrade and log in again by the end of phase 2.
- One more flag on `Client` and one migration.
- Admins who scripted the admin API with RP tokens lose that path. This is intended, and the phase-1 metric shows who they are.

## Alternatives considered

1. **Scope only (`mrwho:admin`), no dedicated audience.** This stops RP→admin, provided the scope is gated per client. CLI tokens would still carry `aud=api`, though, so every RP that accepts `api` would accept them. Rejected: the separation should go both ways.
2. **A `client_id` allow-list only.** It is brittle (a list of client IDs in configuration), and it leaves CLI tokens with `aud=api`. It's used here only as an extra check on top of audience and scope.
3. **Separate signing keys or issuer for admin tokens.** This gives the strongest isolation, but it doubles the key-management and discovery work for little gain over audience+scope checks inside the same trust domain. It could be revisited with F9 (KMS-backed keys).
4. **Cookie-only admin API (drop bearer).** Simplest, but it breaks the CLI and MCP, which are product features.

## Open questions

- **Sender-constraining:** should the CLI's admin tokens be DPoP-bound? `api-bearer` rejects `cnf` today (`ApiTokenAuthHandler.cs:81-82`). That would need DPoP support in the handler and the CLI; it's a candidate follow-up.
- **Keys:** confirm that `TokenValidator`'s key provider (`GetPublicJwksAsync`, `TokenValidator.cs:46`) returns only the *current tenant's* keys. If keys are shared across tenants, the `iss` check in §3 is what stops cross-tenant replay, and it must ship in phase 1.
- **Defaults:** separately from this ADR, the CIBA default audience (hard-coded `"api"`) and the inconsistent default-audience rules across grants should be unified on `ApiAudiences`.
