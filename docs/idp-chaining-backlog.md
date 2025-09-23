# MrWhoOidc.WebAuth – IdP Chaining and JAR Support Backlog

Updated: 2025-09-23

Status legend
- [x] Done
- [~] Pending / In progress
- [ ] Not started

Scope
- Enable IdP chaining: a client can have multiple upstream identity providers (start with OIDC; keep design extensible for SAML later).
- Add inbound JAR (JWT-secured Authorization Request, RFC 9101) for clients calling the authorize endpoint.
- Update Admin UI (Razor Pages in `MrWhoOidc.WebAuth`) and end-user login UI.
- Keep backwards compatibility when no providers are configured.

Key principles
- Provider abstraction with provider-specific config stored as JSON.
- Per-client mapping to zero-or-more providers with ordering and defaulting.
- Dynamic external OIDC handler setup without app restarts.
- Minimal assumptions about libraries; target .NET 9.

Epics and stories

1) Data model, storage, migrations
- [x] Story: Introduce provider abstraction
  - Add table `IdentityProviders`
    - `Id` (PK), `Name` (unique, machine-safe), `DisplayName`, `Type` (enum: OIDC, SAML), `Enabled` (bool), `IsDefault` (bool), `LogoUrl` (nullable), `SortOrder` (int), `ConfigJson` (nvarchar(max) / jsonb), `CreatedAt`, `UpdatedAt`).
  - Add table `ClientIdentityProviders` (many-to-many)
    - `ClientId` (FK to existing Clients), `IdentityProviderId` (FK), composite PK (`ClientId`, `IdentityProviderId`), `Enabled`, `IsDefaultForClient`, `AutoRedirectIfSingle` (bool), `RequiredAcr` (nullable), `Order` (int).
  - Add table `IdentityProviderClaimMappings`
    - `Id` (PK), `IdentityProviderId` (FK), `ExternalClaim` (string), `LocalClaim` (string), `Transform` (nullable expression/enum), `Order`.
  - Add table `IdentityProviderKeys`
    - `Id` (PK), `IdentityProviderId` (FK), `Purpose` (enum: Signing, Encryption), `Jwk` (json), `Alg` (string), `Active` (bool), `CreatedAt`, `ExpiresAt` (nullable), `Kid`.
  - Optional: `ClientKeys` for inbound JAR verification (public keys/JWKS per client).
  - Acceptance: Migrations generated and applied on empty and existing DB; rollback supported.

- [x] Story: OIDC provider config schema
  - Store in `IdentityProviders.ConfigJson` with validation:
    - `Authority`, `DiscoveryUrl` (optional override), `ClientId`, `ClientSecret` (or client assertion key ref), `ResponseType`, `Scopes` (list), `UsePKCE` (bool), `UseJAR` (bool, outbound), `UsePAR` (bool), `RequestedAcrValues` (string), `Prompt`, `ResponseMode`, `ClockSkewSeconds`, `TokenValidation` options (issuer/audience/expiry), `BackChannelLogout` (bool), `ExtraAuthParams` (kvp).
  - Acceptance: Invalid configurations rejected with actionable messages; `Authority` discovery validated on save if reachable.

2) Admin APIs and UI (Razor Pages in MrWhoOidc.WebAuth)
- [ ] Story: Management APIs (admin-only)
  - CRUD for `IdentityProvider`, `ClientIdentityProvider`, `IdentityProviderClaimMappings`, `IdentityProviderKeys`, and optional `ClientKeys`.
  - ProblemDetails for errors; model validation; RBAC policy.

- [~] Story: Admin UI pages (Razor Pages)
  - Done:
    - Providers list/detail/create/edit/delete; config JSON validation with discovery on save.
    - Client ? Providers mapping page: add/update/delete links; order/default/ACR/auto-redirect flags.
    - Edit page: explicit "Test connection" button with discovery excerpt; form posting fixed.
    - Claim mapping editor (CRUD) at `/Admin/Providers/ClaimMappings` with transforms help.
  - Pending:
    - Keys page: provider keys (outbound JAR) and client public keys (inbound JAR) with JWK/PEM import, active flag.
    - Logo upload/select; drag/drop ordering polish.
  - Acceptance: Full CRUD works, validation visible, audit notes recorded.

3) Authorization pipeline updates (IdP chaining)
- [~] Story: Authorize endpoint parameterization
  - Accept custom `idp` and `idp_hint`; standard `login_hint`, `acr_values`.
  - Resolve client ? available providers. If 0: use local login (existing behavior). If 1 and `AutoRedirectIfSingle`: redirect. If >1 and no forced selection: render provider picker page.
  - Preserve `idp`/`idp_hint` and hints with PAR (`request_uri`) to avoid redirect loops.
  - Pending: remember last used provider per client (cookie) and propagate `prompt/max_age/ui_locales` consistently.
  - Acceptance: Routing logic tested across combinations.

- [~] Story: External OIDC sign-in flow
  - Implemented: Custom external OIDC start/callback with PKCE, protected `state` (+ `nonce`), discovery, token exchange, ID token validation via JWKS (issuer/audience/lifetime/nonce), local user provisioning, persistent `iss+sub` linkage (`ExternalIdentities`), claim mapping application, local cookie sign-in, and return to `/authorize`.
  - Pending: Error/cancel handling polish, re-selection flow after upstream cancel, correlation metrics and logs.
  - Acceptance: Round-trip works with at least two OIDC providers.

4) Inbound JAR (clients ? WebAuth)
- [x] Story: Request object parsing/validation
  - Support `request` and `request_uri` in authorize requests.
  - Validate JWT signature against client registered keys (`ClientKeys` or client JWKS), allowed `alg` set; enforce `aud`, `iss`, `exp`, `nbf` checks and replay protection (nonce/jti store, TTL).
  - Merge parameters per RFC 9101 precedence; reject conflicting parameters.
  - Acceptance: Conformance tests for valid/invalid signatures and claims.

- [x] Story: Discovery metadata updates
  - `request_parameter_supported`, `request_uri_parameter_supported`, `request_object_signing_alg_values_supported`.
  - If PAR is added later: `pushed_authorization_request_endpoint`.
  - Acceptance: Well-known document validates with external tools.

5) Optional: Outbound JAR and PAR to upstream IdPs
- [ ] Story: Outbound JAR
  - If provider `UseJAR`, sign upstream auth request with a configured provider key; support at least `RS256`/`PS256` and `kid`.
  - Acceptance: Works against an upstream IdP requiring JAR.

- [ ] Story: Outbound PAR
  - If provider `UsePAR`, push request to upstream PAR endpoint, receive `request_uri`, then redirect using it.
  - Acceptance: Verified with an IdP enforcing PAR.

6) Token issuance and claims
- [~] Story: Subject resolution and auto-provision
  - Implemented: Link external user by `issuer+sub`; basic auto-provision on first sign-in.
  - Pending: Optional email-based linking with confirmation; per-client auto-provision toggle.
  - Acceptance: New and returning users handled without duplicates.

- [~] Story: Claim mapping and propagation
  - Implemented: `IdentityProviderClaimMappings` CRUD UI and `ClaimMappingService` with transforms (copy, trim, case, prefix/suffix, regex, concat); applied during external provisioning.
  - Pending: Add upstream info in our tokens (`idp`, `amr`, `acr`); propagate `auth_time` (partially present) and mapped claims as needed.
  - Acceptance: Downstream clients can consume upstream metadata.

7) Login UI changes (Razor Pages end-user flow)
- [~] Story: Provider picker page
  - Implemented: Minimal provider picker with links to external start; auto-redirect if single provider.
  - Pending: a11y/design polish, remembered provider hint, mobile improvements.
  - Acceptance: Works across themes/branding.

- [ ] Story: Error/edge cases
  - Friendly errors for upstream `access_denied`, `interaction_required`, `invalid_scope`, timeouts.
  - Allow re-selection upon cancel; preserve original authorize request state.
  - Acceptance: Tested with simulated failures.

8) Keys, crypto, and rotation
- [~] Story: Key storage and rotation
  - Store provider keys (for outbound JAR) and client public keys (for inbound JAR). Support rotation and `kid`.
  - Background task to detect upcoming expiry; admin UI to activate/deactivate keys.
  - Acceptance: Rollover without downtime.

- [ ] Story: JWKS endpoints (if needed)
  - Optional public JWKS exposure per provider/client scope for interoperability.
  - Acceptance: JWKS fetch and cache behaviors verified.

9) Telemetry, security, resilience
- [~] Story: Auditing & logging
  - Structured logs for provider selection, upstream start/finish, errors, claim mappings applied; correlation IDs.
  - Redact secrets; PII handling policy.
  - Acceptance: Logs useful for troubleshooting and pass security review.

- [x] Story: Rate limiting & protections
  - Apply rate limits to authorize, callback, token, userinfo, introspection, and PAR paths; CSRF protections on local UI; strict referrer policy.
  - Acceptance: Basic DoS protections in place.

10) Testing and documentation
- [ ] Story: Automated tests
  - Unit: config validation, claim mapping transforms, JAR parsing/validation.
  - Integration: multi-provider flow, picker UI, error recovery, discovery doc.
  - E2E: two upstream OIDC test providers (e.g., Azure AD, Auth0/Okta dev tenants).
  - Acceptance: CI green on .NET 9; critical paths covered.

- [ ] Story: Documentation
  - Admin guide for configuring providers and client mappings; examples for common IdPs.
  - Developer guide: using `idp`, `acr_values`, inbound JAR; discovery examples.
  - Acceptance: New client onboarding without code changes.

Rollout plan
- [x] Phase 1: DB schema + read-only APIs + discovery updates (feature flags off).
- [~] Phase 2: Admin CRUD + single upstream OIDC provider live (external flow working; validation/mappings wired; polish pending).
- [ ] Phase 3: Multiple providers + picker UI + claim mapping.
- [x] Phase 4: Inbound JAR.
- [ ] Phase 5: Optional outbound JAR/PAR.
- [ ] Phase 6: Hardening, audits, perf, docs.

Non-functional requirements
- Backwards compatibility when no providers configured.
- Secrets safety (Key Vault/DPAPI), no plaintext secrets at rest.
- Caching of discovery docs and keys; reasonable timeouts/retries.
- Observability and correlation across upstream/downstream requests.

Next steps (proposed)
- Target milestone: Phase 3 (Multi-provider GA)
- P0 (2 weeks)
  - [ ] Management APIs (admin-only): CRUD for `IdentityProvider`, `ClientIdentityProvider`, `IdentityProviderClaimMappings`, `IdentityProviderKeys`, `ClientKeys`; RBAC policy; ProblemDetails.
  - [ ] Keys UI pages: import JWK/PEM, validate `kid`/`alg`, set `Active`, rotate/activate flows; basic JWKS preview.
  - [ ] Authorize pipeline: remember last provider per client (cookie); propagate `prompt`, `max_age`, `ui_locales`; preserve `idp`/hints across PAR/request_uri.
  - [ ] External OIDC error/cancel handling: friendly errors, retry/return to picker, correlation IDs in logs.
  - [ ] Provider picker polish: remembered provider hint, a11y fixes, mobile layout.
  - [ ] Tests: unit (claim transforms, JAR merge rules, idp selection), integration (two OIDC providers happy path, cancel), discovery doc verification; wire into CI.
  - [ ] Docs: Admin guide draft (providers, mappings, keys), Developer guide draft (authorize params, inbound JAR).
- P1 (next 2–4 weeks)
  - [ ] JWKS endpoints (optional) for provider/client scopes; caching and `kid` rotation story.
  - [ ] Telemetry: structured logging and basic metrics (start/callback durations, errors, cancellations); redact PII.
  - [ ] Outbound JAR: sign upstream auth requests when `UseJAR`; key selection by `kid`.
  - [ ] Outbound PAR: push to PAR endpoint when `UsePAR`; fallback behavior.
  - [ ] Subject linking options: email-based linking (opt-in) and per-client auto-provision toggle.

Risks and decisions
- Decide whether to expose JWKS publicly or rely on admin-imported keys only for inbound JAR.
- Confirm acceptable `alg` set for inbound JAR (e.g., RS256/PS256/ES256) and enforce.
- Validate secrets handling approach (Key Vault/DPAPI) before enabling client-provided secrets.

Test matrix (Phase 3)
- Two OIDC providers (e.g., Azure AD + Auth0/Okta): success, cancel, error scopes.
- Single-provider auto-redirect on and off.
- With and without inbound JAR; with and without PAR request_uri; propagation of hints/params.
- Rotation of client/provider keys with `kid` changes.

Appendix: Minimal OIDC ConfigJson example
```json
{
  "Authority": "https://login.example.com",
  "ClientId": "mrwho-webauth",
  "ClientSecret": "<secret or null when using client assertion>",
  "ResponseType": "code",
  "Scopes": ["openid", "profile", "email"],
  "UsePKCE": true,
  "UseJAR": false,
  "UsePAR": false,
  "RequestedAcrValues": "",
  "Prompt": null,
  "ResponseMode": null,
  "ClockSkewSeconds": 120,
  "TokenValidation": { "ValidateIssuer": true, "ValidateAudience": false, "ValidateLifetime": true },
  "BackChannelLogout": true,
  "ExtraAuthParams": { }
}
