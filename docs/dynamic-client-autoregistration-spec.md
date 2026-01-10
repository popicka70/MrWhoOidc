# Dynamic Client Auto‑Registration (RFC 7591/7592) — Requirements & Spec

Status: Draft (decisions captured; ready for implementation planning)

## Decision summary (Jan 10, 2026)
- Compliance target: strict RFC 7591/7592 alignment (with an explicit supported metadata profile).
- Disabled behavior: return `400` with `error=invalid_request` (do not hide with `404`).
- Discovery: advertise `registration_endpoint` only when tenant realm is configured and DCR is effectively enabled.
- Initial access token (IAT): always required; managed via Platform Settings UI.
- Tenant permissions: tenant admin can set the tenant’s DCR realm.
- Single-tenant mode: realm selection lives on Platform Settings page.
- Admin UI: dynamically registered clients must be visibly distinguished.
- Client secrets: never-expire as today.
- Realm selection: tenants may choose any realm.

## Context
This repo already implements OAuth 2.0 Dynamic Client Registration (DCR) and Client Configuration Management:

- `POST /register` (RFC 7591)
- `GET|PUT|DELETE /register/{client_id}` (RFC 7592)

Implementation entry points today:
- Handler logic: `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs`, `MrWhoOidc.WebAuth/Handlers/ClientConfigurationHandler.cs`
- Discovery advertising: `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`
- Global config flags: `MrWhoOidc.Auth/Services/AuthOptions.cs`
- Per-tenant “realm to assign dynamic clients to”: `TenantSettings.Auth.DynamicClientRegistrationRealmId` in `MrWhoOidc.Auth/Settings/TenantSettings.cs`
- Platform settings UI: `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml` (currently QR-only)
- Platform tenant edit UI already includes realm selection for DCR: `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Edit.cshtml`

This document specifies:
1) how the feature is enabled/disabled via **Platform Settings UI**,
2) how each tenant chooses the **realm that will host auto-registered clients**, and
3) the intended compliance posture against RFC 7591/7592.

## Goals
- Provide an operator-controlled, runtime toggle for DCR (no redeploy required).
- Ensure each tenant can decide **where (which realm)** auto-registered clients land, or disable DCR for that tenant.
- Keep discovery metadata truthful per tenant (only advertise `registration_endpoint` when actually usable).
- Produce a clear compliance checklist and identify gaps.

## Non-goals
- Adding dependencies on OpenIddict / Microsoft Identity Platform (explicitly forbidden in this repo).
- Full OIDC certification/conformance work beyond DCR (unless explicitly requested).

## Terminology
- **DCR**: Dynamic Client Registration (`/register`).
- **Platform**: system-wide operator scope (platform-admin).
- **Tenant**: customer / organization boundary.
- **Realm**: grouping inside a tenant; clients belong to a realm.

## Requirements

### R1 — Platform-level enable/disable (UI)
- The operator can turn DCR on/off from the Platform Settings page.
- When OFF:
  - `POST /register` must not accept registrations.
  - `GET|PUT|DELETE /register/{client_id}` must not be usable.
  - Discovery for all tenants must **not** advertise `registration_endpoint`.
- When ON:
  - DCR availability is still controlled by per-tenant realm configuration (R2).

#### R1.1 — Initial access token requirement (UI)
- The OP must always require an initial access token (Bearer) for `POST /register`.
- The initial access token policy must be controlled via Platform Settings UI (token management), not via appsettings-only config.
- Operators can rotate/revoke tokens.

Implementation note (strictness): This is stricter than RFC 7591’s optional “Initial Access Token” mechanism, but remains compliant.

### R2 — Per-tenant “realm for auto-registered clients” (UI)
- Each tenant can choose **which realm** newly auto-registered clients are assigned to.
- Selecting “no realm” disables DCR for that tenant.
- The realm selection must be validated (exists, belongs to the tenant).

#### R2.1 — Tenant permissions
- Only tenant admins can change the tenant’s DCR realm.

### R3 — Single-tenant environments (no tenant UI)
- In single-tenant mode, tenant-facing configuration UI is not exposed.
- The operator must still be able to configure:
  - platform DCR enable/disable (R1)
  - the “realm for auto-registered clients” for the single/default tenant (R2 equivalent)

Decision: configure the single/default tenant realm on the Platform Settings page.

### R4 — Compliance checklist and gaps
- We must be able to review the implementation against a defined target:
  - **Minimum compliance**: safe subset with clear documentation.
  - **Stricter compliance**: closer alignment to RFC 7591/7592 normative behaviors.

## Design: Effective enablement model
DCR should be considered enabled for a specific tenant only when all of these are true:

1) **Build/config**: `AuthOptions.EnableDynamicClientRegistration == true`
2) **Platform runtime**: `PlatformSettings.DynamicClientRegistrationEnabled == true` (new)
3) **Tenant realm configured**: `TenantSettings.Auth.DynamicClientRegistrationRealmId != null`
4) (Optional) **License/feature**: if you decide DCR is license-gated in the future.

Additionally, even when (1)-(3) are true:
- `POST /register` must require a valid initial access token.

### Discovery advertising
Discovery should include `registration_endpoint` only if (1)+(2)+(3) are true for the current tenant context.

## Data model changes (proposed)

### Platform settings
Add a new boolean field:
- `PlatformSettings.DynamicClientRegistrationEnabled` (default: `false`)

Add platform-managed initial access token storage (UI-managed):
- New table/entity (suggested): `PlatformInitialAccessToken`
   - `Id` (UUIDv7)
   - `TokenHash` (SHA-256 base64 or base64url)
   - `CreatedAt`, `RevokedAt?`, `CreatedBy?`, `Description?`
   - Optional: `LastUsedAt?` (audit)

Rationale:
- `AuthOptions.InitialAccessTokenHashes` is currently appsettings-driven; that conflicts with the requirement that platform UI controls the policy.

Rationale:
- Today DCR enablement is controlled only by `AuthOptions.EnableDynamicClientRegistration`, which is config-driven.
- Platform settings are already persisted in DB and editable via UI.

### Tenant settings
Already present:
- `TenantSettings.Auth.DynamicClientRegistrationRealmId` (nullable, null disables tenant DCR)

No additional tenant fields are required unless you want a separate per-tenant enable flag.

## UI/UX

### Platform Admin: Platform Settings
Location: `/platform-admin/settings`

Add a new section (suggested placement under “Authentication Options”):
- Switch: “Enable Dynamic Client Registration (RFC 7591/7592)”
- Help text:
  - Security warning (DCR expands attack surface).
   - Initial access tokens are always required.
  - Clarify that per-tenant realm configuration still gates behavior.

Add a subsection: “Dynamic Client Registration — Initial Access Tokens”
- List existing tokens (do not show plaintext), with create/revoke actions.
- Create action generates a new random token and displays it once.
- Store only the hash.

Single-tenant mode subsection (only when multi-tenancy is disabled):
- Dropdown: “Realm for auto-registered clients” (Disabled + realm list for default tenant).

Optional (nice-to-have):
- Read-only display of current config-only flags:
  - `AuthOptions.RequireInitialAccessToken`
  - `AuthOptions.EnableClientConfigurationEndpoint`
  - `AuthOptions.RegistrationAccessTokenLifetimeSeconds`

### Platform Admin: Tenant Edit (already present)
Location: `/platform-admin/tenants/{id}`

This page already supports:
- Dropdown for “Dynamic Client Registration realm” including “Disabled (no realm)”

Spec requirement:
- Keep as source of truth for platform-admin in multi-tenant deployments.

### Tenant Admin: Tenant Settings (new UI)
Location: `/t/{tenantSlug}/admin/settings` (or a new page under `/admin/security`)

Add a new section:
- Dropdown: “Dynamic Client Registration realm”
  - Values: “Disabled (no realm)” + all realms in tenant
- Behavior:
- Behavior:
   - Only visible when multi-tenancy is enabled.
   - Only tenant admins can change the selection.
   - Save updates to `Tenant.SettingsJson` → `TenantSettings.Auth.DynamicClientRegistrationRealmId`.

### Single-tenant mode UI
Decision:
- Configure the DCR realm for the default tenant on the Platform Settings page.

## Endpoint behavior (normative)

### POST /register
Input:
- Accept JSON body; require `redirect_uris`.

Output:
- On success: `201` with `client_id` and (if applicable) `client_secret` returned once.
- Return `registration_access_token` and `registration_client_uri` if configuration management is enabled.

Gating:
- If global/platform disabled: reject with `400` (`error=invalid_request`).
- If tenant has no configured realm: reject with `400` (`error=invalid_request`).
- If missing/invalid initial access token: reject with RFC-appropriate `401` (`error=invalid_token`).

### GET/PUT/DELETE /register/{client_id}
- Require a valid `registration_access_token`.
- Token must be bound to `(tenant, client_id)`.

## Compliance checklist (initial)
This is the checklist we will use to judge “complies with requirements”. It intentionally mixes RFC expectations and security/operator expectations.

### Discovery truthfulness
- Only advertise `registration_endpoint` when the endpoint is actually usable for the current tenant.

Acceptance criteria:
- If tenant realm is not configured, discovery for that tenant omits `registration_endpoint`.
- If platform DCR is off, discovery omits `registration_endpoint` for all tenants.

### Access control / abuse resistance
- Rate limiting on `/register`.
- Consider requiring an initial access token in production.
- Consider IP allow-listing / additional throttling (operator decision).

### Registration access tokens
- Honor configured lifetime if non-zero (currently appears not enforced).
- Store token hashes; compare safely.

Acceptance criteria:
- When lifetime > 0, store `ExpiresAt = CreatedAt + lifetime` and enforce expiry for RFC 7592 calls.
- When lifetime == 0, tokens never expire.

### Metadata validation
At minimum:
- `redirect_uris` required and validated.
- Ensure `token_endpoint_auth_method` is supported and consistent with provided key material when using `private_key_jwt`.

### Client persistence
- Persist metadata fields that are echoed back, or explicitly document that the server ignores them.

Strict alignment rule:
- The server must not echo back metadata it does not store.
- For unsupported metadata fields: reject the registration request with `error=invalid_client_metadata`.

Supported metadata profile (initial, to be implemented for strict mode):
- `redirect_uris`
- `token_endpoint_auth_method`
- `grant_types`, `response_types`
- `jwks_uri` and/or `jwks` (with clear validation rules)
- OIDC preferences already persisted on `Client` (id_token/userinfo encryption/signing preferences)
- Logout metadata already persisted on `Client` (front/back-channel logout URIs and flags)
- `post_logout_redirect_uris`

Not supported (reject with `invalid_client_metadata` unless and until implemented):
- `software_statement`
- `client_uri`, `logo_uri`, `contacts`, `tos_uri`, `policy_uri`
- `scope` (until persisted and enforced)
- `request_object_*`, `default_max_age`, `require_auth_time`, `default_acr_values`, `initiate_login_uri`, `request_uris` (until persisted and enforced)

### Configuration management (RFC 7592)
- Ensure GET/PUT/DELETE behaviors match your chosen compliance target.
- Ensure secrets are not returned from GET/PUT.

## Noted gaps vs intended requirements (based on current code)
These are observations to validate; they inform the work plan.

- Platform UI toggle does not exist yet; enablement is config-only (`AuthOptions.EnableDynamicClientRegistration`).
- Discovery currently advertises `registration_endpoint` when global config is enabled, even if the tenant has not enabled DCR via realm selection.
- Initial access tokens are currently appsettings-only (`AuthOptions.InitialAccessTokenHashes`) rather than UI-managed.
- `AuthOptions.RegistrationAccessTokenLifetimeSeconds` exists but registration tokens appear stored with `ExpiresAt = null` (no expiry).
- `AuthOptions.DynamicClientAllowedSchemes` and `DynamicClientAllowLocalhostHttp` exist but are not used in redirect URI validation.
- Many RFC 7591 metadata fields are echoed but not persisted/validated; for strict mode these must be rejected or implemented.
- Admin UI does not currently distinguish DCR-created clients; a persisted marker is required.

## Admin UI requirements

### Distinguish DCR-created clients
- Clients created via `POST /register` must be visibly distinguished in the admin UI (list + details).
- Recommended persisted marker:
  - Add `Client.ProvisioningSource` (enum/string) with value `DynamicClientRegistration`.

Optional enrichments (if you want fuller operator visibility later):
- Store `software_id` and `software_version` when provided.
- Store `client_id_issued_at` for RFC-accurate responses.
- Store last RFC 7592 update timestamp.

## Implementation plan (high level)
- Add `PlatformSettings.DynamicClientRegistrationEnabled` + UI toggle on Platform Settings page.
- Add platform-managed initial access token storage and UI management.
- Update discovery and handlers to consult platform setting and tenant realm enablement, and to require initial access tokens.
- Add tenant-facing UI to set `dynamicClientRegistrationRealmId` when multi-tenant mode is enabled.
- Enforce registration access token lifetimes and redirect URI scheme policy.
- Add a persisted “created via DCR” marker and surface it in admin UI.

