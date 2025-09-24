# MrWhoOidc.WebAuth � IdP Chaining and JAR Support Backlog

Updated: 2025-09-24

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
- [x] Story: Management APIs (admin-only)
  - CRUD for `IdentityProvider`, `ClientIdentityProvider`, `IdentityProviderClaimMappings`, `IdentityProviderKeys`, and `ClientKeys` implemented under `/admin/api` with RBAC policy and rate limits.
  - ProblemDetails on errors; model validation; kid uniqueness checks; JWKS basic validation and history hashing.

- [~] Story: Admin UI pages (Razor Pages)
  - Done:
    - Providers list/detail/create/edit/delete; config JSON validation with discovery on save.
    - Client ? Providers mapping page: add/update/delete links; order/default/ACR/auto-redirect flags.
    - Edit page: explicit "Test connection" button with discovery excerpt; form posting fixed.
    - Claim mapping editor (CRUD) at `/Admin/Providers/ClaimMappings` with transforms help.
    - Provider keys page: import private JWK JSON, `kid`/`alg`, `Active` toggle, activate/deactivate and delete.
    - Client keys page: JWKS URI fetch + save, manual JWKS JSON edit, history with hash, duplicates check, basic summary (key count).
  - Pending:
    - Keys: PEM import (convert to JWK), nicer pretty-print/compact toggles, richer JWKS preview.
    - Logo upload/select; drag/drop ordering polish.
  - Acceptance: Full CRUD works, validation visible, audit notes recorded.

3) Authorization pipeline updates (IdP chaining)
- [x] Story: Authorize endpoint parameterization
  - Accept custom `idp` and `idp_hint`; standard `login_hint`, `acr_values`, `prompt`, `max_age`, `ui_locales`.
  - Resolve client ? available providers. If 0: use local login (existing). If 1 and `AutoRedirectIfSingle`: redirect. If >1 and no forced selection: render provider picker.
  - Preserve/round-trip hints across PAR (`request_uri`) and JAR; sanitize address bar when using PAR.
  - Remember last used provider per client via cookie and prefer it when not forcing `select_account`.
  - Acceptance: Routing logic tested across combinations.

- [~] Story: External OIDC sign-in flow
  - Implemented: Custom external OIDC start/callback with PKCE, protected `state` (+ `nonce`), discovery, token exchange, ID token validation via JWKS (issuer/audience/lifetime/nonce), local user provisioning, persistent `iss+sub` linkage (`ExternalIdentities`), claim mapping application, local cookie sign-in, and return to `/authorize`.
  - Implemented: Friendly error/cancel handling page with correlation ID and "Choose a different provider" link back to picker.
  - Pending: Correlation metrics/logs and upstream cancel telemetry; re-selection after upstream cancel is supported but needs UX polish.
  - Acceptance: Round-trip works with at least two OIDC providers.

4) Inbound JAR (clients ? WebAuth)
- [x] Story: Request object parsing/validation
  - Support `request` and `request_uri` in authorize requests.
  - Validate JWT signature against client registered keys (`ClientKeys` or client JWKS), allowed `alg` set; enforce `aud`, `iss`, `exp`, `nbf` checks and replay protection (nonce/jti store, TTL).
  - Merge parameters per RFC 9101 precedence; reject conflicting parameters.
  - Replay: In-memory `jti/nonce` replay cache implemented with TTL and optional Redis-backed distributed cache when configured; TTL configurable via `AuthOptions`.
  - Acceptance: Conformance tests for valid/invalid signatures and claims.

- [x] Story: JARM authorization responses
  - Support `response_mode` values `query.jwt` and `form_post.jwt` for success and error.
  - Optional JWE encryption using client RSA key (`RSA-OAEP` + `A256GCM`) selected from client JWKS (prefers `use=enc`).
  - Discovery advertises signing/encryption capabilities.

- [x] Story: Discovery metadata updates
  - `request_parameter_supported`, `request_uri_parameter_supported`, `request_object_signing_alg_values_supported`.
  - If PAR is added later: `pushed_authorization_request_endpoint`.
  - Acceptance: Well-known document validates with external tools.

5) Optional: Outbound JAR and PAR to upstream IdPs
- [x] Story: Outbound JAR
  - If provider `UseJAR`, sign upstream auth request with a configured provider key; support at least `RS256`/`PS256` and `kid`.
  - Acceptance: Works against an upstream IdP requiring JAR.

- [x] Story: Outbound PAR
  - If provider `UsePAR`, push to upstream PAR endpoint, receive `request_uri`, then redirect using it.
  - Acceptance: Verified with an IdP enforcing PAR.

6) Token issuance and claims
- [~] Story: Subject resolution and auto-provision
  - Implemented: Link external user by `issuer+sub`; basic auto-provision on first sign-in.
  - Pending: Optional email-based linking with confirmation; per-client auto-provision toggle.
  - Acceptance: New and returning users handled without duplicates.

- [~] Story: Claim mapping and propagation
  - Implemented: `IdentityProviderClaimMappings` CRUD UI and `ClaimMappingService` with transforms (copy, trim, case, prefix/suffix, regex, concat); applied during external provisioning.
  - Implemented: Default mappings fallback via `AuthOptions.DefaultClaimMappings` when a provider has no explicit mappings.
  - Implemented: Mapping source now includes upstream `acr` and aggregated `amr` (space-delimited) alongside common claims; used during external provisioning.
  - Implemented: Include `idp` and `acr` from the upstream provider in issued tokens; propagate `auth_time` from the upstream sign-in when available.
  - Pending: Emit `amr` consistently (and map where applicable); extend mapped claim propagation into issued tokens where policy allows (wire via `AuthorizationCodeMetadataStore`).
  - Acceptance: Downstream clients can consume upstream metadata.

7) Login UI changes (Razor Pages end-user flow)
- [~] Story: Provider picker page
  - Implemented: Minimal provider picker with links to external start; auto-redirect if single provider; honors `idp_hint` and remembers last provider per client.
  - Pending: a11y/design polish, mobile improvements.
  - Acceptance: Works across themes/branding.

- [~] Story: Error/edge cases
  - Friendly errors for upstream `access_denied`, `interaction_required`, `invalid_scope`, timeouts (basic page implemented with correlation id and reselect link).
  - Allow re-selection upon cancel; preserve original authorize request state.
  - Acceptance: Tested with simulated failures.

8) Keys, crypto, and rotation
- [~] Story: Key storage and rotation
  - Store provider keys (for outbound JAR) and client public keys (for inbound JAR). Support rotation and `kid`.
  - Background task to detect upcoming expiry; admin UI to activate/deactivate keys.
  - Status: Storage + admin UI are present; expiry detection/alerts pending.
  - Acceptance: Rollover without downtime.

- [ ] Story: JWKS endpoints (if needed)
  - Optional public JWKS exposure per provider/client scope for interoperability.
  - Acceptance: JWKS fetch and cache behaviors verified.

9) Telemetry, security, resilience
- [~] Story: Auditing & logging
  - Structured logs for provider selection, upstream start/finish, errors, claim mappings applied; correlation IDs.
  - Redact secrets; PII handling policy.
  - Status: Metrics and correlation IDs used in `/authorize` (duration, request/JAR sizes, mode buckets, PAR consumption). Expand across external flow and admin APIs.
  - Acceptance: Logs useful for troubleshooting and pass security review.

- [x] Story: Rate limiting & protections
  - Apply rate limits to authorize, callback, token, userinfo, introspection, and PAR paths; CSRF protections on local UI; strict referrer policy.
  - Acceptance: Basic DoS protections in place.

10) Testing and documentation
- [~] Story: Automated tests
  - Unit present: config validation, claim mapping transforms, JAR parsing/validation, client assertion, PAR store, auth code, key rotation, token service, admin providers API, realm/role assignments.
  - Integration: multi-provider mapping logic tests; PAR stress tests exist; add two OIDC providers E2E when feasible.
  - E2E: TODO for two upstream OIDC test providers (e.g., Azure AD, Auth0/Okta dev tenants).
  - Acceptance: CI green on .NET 9; critical paths covered.

- [ ] Story: Documentation
  - Admin guide for configuring providers and client mappings; examples for common IdPs.
  - Developer guide: using `idp`, `acr_values`, inbound JAR; discovery examples.
  - Acceptance: New client onboarding without code changes.

11) On-Behalf-Of (OBO) / Token Exchange (RFC 8693)
- [x] MVP scope and constraints
  - Single-hop delegation only (subject token must not itself contain `act`).
  - Bearer-only for MVP: if subject token is DPoP-bound (`cnf.jkt`), deny exchange (no bridging) and return `invalid_request` with description.
  - Supported subject token types: local access tokens issued by this AS (JWT or opaque).
  - Supported requested token type: access token (default). Other token types rejected.
  - Feature flag: `Auth:Features:EnableTokenExchange` controls exposure and discovery advertisement.
  - Implemented: Feature flag added; discovery advertises grant when enabled; token service enforces single-hop and denies DPoP bridging; supports local JWT/opaque subject tokens and only issues access tokens.

- [~] Story: Token Exchange grant at `/token`
  - Endpoint behavior
    - Parse and validate: Implemented (grant_type, subject_token, optional types, optional `audience`/`resource` with conflict check, optional `scope`).
    - Authenticate client (existing client auth methods). Require confidential clients unless `private_key_jwt` policy allows otherwise. Pending stricter enforcement.
    - Rate limit: apply `rl-token-exchange` policy (per client_id) and global burst control. Pending.
  - Subject token validation
    - If JWT: validate signature (local JWKS), `iss`, `exp/nbf`; audience validation pending; reject if `act` claim present (single-hop).
    - If opaque: look up in DB; must be active (not expired/revoked). Use stored `audience`, `scope`, `cnf` for policy.
    - If DPoP-bound (has `cnf.jkt`): for MVP return error `invalid_request` (`dpop_bridging_not_supported`). Phase 2: add bridging policy (see below).
  - Policy enforcement (delegation)
    - Resolve caller�s OBO policy (per-target client or global default) to ensure:
      - Caller is allow-listed to perform exchanges.
      - Subject audience is allowed as source.
      - Requested target audience/resource is allowed (or default). If both `audience` and `resource` provided and differ, reject.
      - Scope narrowing: resulting scopes = intersection(subject_scopes, caller_allowed_scopes_for_target, requested_scopes). If empty, return `insufficient_scope`.
      - Max lifetime: cap new token lifetime to min(policy_max, subject_remaining_lifetime, global default).
    - Pending: Policy model and enforcement not yet implemented (current narrowing = subject ∩ requested; simple audience allow-list via server `ApiAudiences`).
  - Issuance
    - Preserve end-user identity: new access token `sub` = subject token `sub`.
    - Add `act` claim with actor info (at minimum `{ "sub": <caller_client_id> }`).
    - Include `aud` = requested or policy default; include `scope` as narrowed set.
    - If opaque tokens are enabled for that audience, persist opaque access token. Pending: persist `ActJson`/`DelegationDepth` fields when policy schema lands. For JWT, `act` claim included.
  - Error handling
    - RFC-compliant errors: `invalid_request`, `invalid_grant`, `unauthorized_client`, `insufficient_scope`, `invalid_target`, `unsupported_token_type`.
    - For DPoP bridging denied: `invalid_request` with `error_description="dpop_bridging_not_supported"`.
  - Acceptance
    - Exchange succeeds for allowed caller/source_aud/target_aud; new access token contains `act` and narrowed scopes.
    - Exchange rejected with correct error when policy disallows or validations fail.

- [ ] Story: Delegation policy model + Admin UI
  - Data model (EF migration)
    - Add columns to `Clients` (simple MVP; can be moved to dedicated tables later):
      - `OboEnabled` (bool), `OboAllowedCallersJson` (string[] of caller client_ids),
      - `OboAllowedSourceAudiencesJson` (string[]), `OboAllowedTargetAudiencesJson` (string[]),
      - `OboAllowedScopesJson` (string[]), `OboMaxDelegationDepth` (int, default 1),
      - `OboMaxLifetimeMinutes` (int, default 15), `OboDpopMode` (enum: `Deny`, `RequireSameJkt`, `AllowSameJktOnly`).
    - Token persistence (opaque): extend `Tokens` with `ActJson` (json), `DelegationDepth` (int, default 0). Ensure indexes on `Type`, `Audience`, `RevokedAt` remain performant.
  - Service layer
    - `IOboPolicyService` to load/validate policy for a caller and target audience.
    - `ITokenService.CreateDelegatedAccessTokenAsync(...)` to issue delegated tokens with `act` and narrowing.
  - Admin API/UI
    - Admin API endpoints under `/admin/api/obo-policies` or reuse Clients API with OBO subresource.
    - Razor Pages: per-client OBO settings editor (enable, callers allow-list, source/target audiences, allowed scopes, lifetime, DPoP mode). Include help and validation hints.
  - Acceptance
    - Policies persisted and enforced by `/token` exchange path. UI prevents invalid combinations and shows validation messages.

- [x] Story: Discovery metadata updates (OAuth 2.0 AS Metadata)
  - Add `urn:ietf:params:oauth:grant-type:token-exchange` to `grant_types_supported` when feature flag enabled.
  - Document any non-standard metadata separately; keep discovery minimal.
  - Acceptance: External tools accept well-known; clients can discover token-exchange support.

- [ ] Story: Introspection/UserInfo shaping for delegation
  - Introspection: include `act` claim when present (for both JWT and opaque). Ensure privacy shaping policy does not leak actor details to unauthorized callers.
  - UserInfo: unchanged by default; optionally include actor info only for trusted clients (future).
  - Acceptance: Responses reflect delegation appropriately without leaking PII.

- [ ] Story: Telemetry, rate limits, and auditing
  - Metrics: `token_exchange_requests`, `token_exchange_success`, `token_exchange_failures` counters with tags: `outcome`, `source_token_type` (jwt/opaque), `dpop_mode`, `target_aud` (bucketized), `client_bucket`.
  - Histogram: `token_exchange_duration_ms`.
  - Logs: structured audit entries including correlation id, hashed/bucketized `client_id`, source/target aud, outcome.
  - Rate limiting: policy `rl-token-exchange` per client; denial returns appropriate `Retry-After` when applicable.
  - Acceptance: Metrics visible in dashboards; rate limits applied; audit logs usable for investigations.

- [~] Story: Tests and samples
  - Unit tests
    - Added: Happy path (JWT subject) with scope narrowing and `act` claim; DPoP bridging denied when `cnf.jkt` present.
    - Pending: Opaque subject variant; single-hop rejection when `act` present; audience narrowing failures; lifetime cap; policy matrix.
  - Integration tests
    - Happy path: API A issued token -> exchange to API B by allowed client -> new token works for B.
    - Unauthorized client, disallowed source/target audience, `insufficient_scope`.
    - Opaque and JWT subject token variants.
  - Samples/docs
    - Minimal "service calls API on behalf of user" sample with Blazor front-end + API-to-API call path.
  - Acceptance: CI green on .NET 9; core OBO paths covered.

- [ ] Story: Phase 2 � DPoP bridging and fidelity
  - DPoP bridging policy
    - `RequireSameJkt`: require DPoP proof and bind new token to same `jkt` as subject token; verify proof on `/token` with `ath` hashed over `subject_token`.
    - `AllowSameJktOnly`: allow bridging only when proof is present and matches; otherwise reject.
  - Optional: accept ID tokens as `subject_token` when policy allows (constrained audiences, short lifetimes).
  - Optional: consent integration for exchange (reuse existing consent model with exchanged scopes).
  - Acceptance: Bridging mode works end-to-end; discovery unchanged; security review done.

12) Machine-to-Machine (M2M) / Client Credentials
- [x] Story: Client Credentials grant at `/token`
  - Implemented: `/token` handles `grant_type=client_credentials` with `client_secret_basic`/`client_secret_post` and `private_key_jwt`; audience vs resource validation; scope allow-list via `ClientScopes`; JWT issuance (15 min); optional DPoP binding via `cnf.jkt`; includes `client_id`/`sub` and optional `realm` claim.
  - Pending: Optional mTLS per client; configurable lifetime/format per client.
  - Acceptance: End-to-end issuance succeeds for allowed scopes/audiences; rejected for unauthorized scope/audience or failed client auth.

- [~] Story: M2M policy model + Admin UI
  - Current: Enforcement uses DB `ClientScopes` and server `ApiAudiences`; no dedicated Admin UI for M2M policy yet.
  - TODO: Per-client allowed scopes/audiences UI, token lifetime/format, required auth methods (secret vs `private_key_jwt`), optional mTLS thumbprints, per-client rate limits.
  - Acceptance: Policies persisted and enforced; UI CRUD complete.

- [x] Story: Discovery metadata updates
  - Advertise `grant_types_supported` including `client_credentials`; `token_endpoint_auth_methods_supported` and signing alg values; DPoP capability hints.
  - Acceptance: Well-known validates; clients can discover M2M.

- [~] Story: Telemetry, rate limits, and auditing for M2M
  - Metrics include `grant_type=client_credentials`; token endpoint rate limits applied globally; logs include basic warnings/errors.
  - TODO: Add richer structured logs/metrics (audience/scope buckets) specifically for M2M flows; redact PII.
  - Acceptance: Useful for troubleshooting; DoS protections in place.

- [~] Story: Tests and samples (M2M)
  - Sample: Blazor page `/m2m-test` issues `client_credentials` token and calls protected API.
  - TODO: Unit tests (scope/audience validation, auth method checks, DPoP accepted/rejected) and integration tests; sample docs.
  - Acceptance: CI green on .NET 9; critical M2M paths covered.

Rollout plan
- [x] Phase 1: DB schema + read-only APIs + discovery updates (feature flags off).
- [x] Phase 2: Admin CRUD + single upstream OIDC provider live (external flow working; validation/mappings wired; polish pending).
- [~] Phase 3: Multiple providers + picker UI + claim mapping (functional; UX polish/tests pending).
- [x] Phase 4: Inbound JAR.
- [x] Phase 5: Optional outbound JAR/PAR.
- [ ] Phase 6: Hardening, audits, perf, docs.

Non-functional requirements
- Backwards compatibility when no providers configured.
- Secrets safety (Key Vault/DPAPI), no plaintext secrets at rest.
- Caching of discovery docs and keys; reasonable timeouts/retries.
- Observability and correlation across upstream/downstream requests.

Next steps (proposed)
- Target milestone: Phase 3 (Multi-provider GA)
- P0 (2 weeks)
  - [ ] Keys UI: PEM import (convert to JWK), pretty-print/compact toggle; strengthen JWKS validation (alg/kty/use checks).
  - [ ] External OIDC UX: add structured logs/metrics with correlation IDs; refine friendly errors (localization), cancel/timeout telemetry.
  - [ ] JAR hardening: enable Redis-backed replay cache in production (already supported via DI when Redis is configured); tune TTL/clock skew via `AuthOptions`.
  - [ ] Provider picker polish: remembered provider hint UI, a11y fixes, mobile layout.
  - [ ] Tests: add integration (two OIDC providers happy path + cancel), discovery doc verification; wire into CI gates for PRs.
  - [ ] Docs: Admin guide draft (providers, mappings, keys), Developer guide draft (authorize params, inbound JAR/JARM response modes).
  - [ ] Discovery: align `request_object_signing_alg_values_supported` with the allowed alg set (currently RS256/PS256/ES256/ES384/ES512 allowed in `AuthOptions`).
- P1 (next 2�4 weeks)
  - [ ] JWKS endpoints (optional) for provider/client scopes; caching and `kid` rotation story.
  - [ ] Telemetry: structured logging and basic metrics (start/callback durations, errors, cancellations) across external flow and admin APIs; redact PII.
  - [x] Outbound JAR: sign upstream auth requests when `UseJAR`; key selection by `kid`.
  - [x] Outbound PAR: push to PAR endpoint when `UsePAR`; fallback behavior.
  - [ ] Subject linking options: email-based linking (opt-in) and per-client auto-provision toggle.
  - [ ] OBO/Token Exchange MVP: implement grant, minimal policy (allow-list callers + audience narrowing), `act` claim, discovery update; limit to single-hop and bearer-only (no DPoP bridging) initially.
  - [ ] M2M polish: Admin UI & policy (allowed scopes/audiences, auth methods, token lifetime/format, optional mTLS), tests and sample docs, discovery validation.

Risks and decisions
- Decide whether to expose JWKS publicly or rely on admin-imported keys only for inbound JAR.
- Confirm acceptable `alg` set for inbound JAR (e.g., RS256/PS256/ES256) and enforce (per-client allow-list supported).
- Validate secrets handling approach (Key Vault/DPAPI) before enabling client-provided secrets.
- OBO: Decide on DPoP bridging semantics (deny vs require proof and carry `cnf`), max delegation depth, and whether ID tokens are accepted as `subject_token`.
- M2M: Decide on `audience` vs `resource` param, single vs multiple audiences, `sub` value (client_id vs URN), required auth methods (secret vs `private_key_jwt`), and mTLS policy.

Test matrix (Phase 3)
- Two OIDC providers (e.g., Azure AD + Auth0/Okta): success, cancel, error scopes.
- Single-provider auto-redirect on and off.
- With and without inbound JAR; with and without PAR request_uri; propagation of hints/params.
- Rotation of client/provider keys with `kid` changes.
- JARM response modes (`query.jwt`, `form_post.jwt`) success and error paths.
- OBO: token exchange success (audience narrowing), disallowed audience/client, DPoP-bound subject token behavior.
- M2M: client_credentials to allowed API audience, invalid scope/audience, DPoP/mTLS variations.

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
