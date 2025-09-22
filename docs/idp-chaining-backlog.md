# MrWhoOidc.WebAuth – IdP Chaining and JAR Support Backlog

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
- Story: Introduce provider abstraction
  - Add table `IdentityProviders`
    - `Id` (PK), `Name` (unique, machine-safe), `DisplayName`, `Type` (enum: OIDC, SAML), `Enabled` (bool), `IsDefault` (bool), `LogoUrl` (nullable), `SortOrder` (int), `ConfigJson` (nvarchar(max) / jsonb), `CreatedAt`, `UpdatedAt`.
  - Add table `ClientIdentityProviders` (many-to-many)
    - `ClientId` (FK to existing Clients), `IdentityProviderId` (FK), composite PK (`ClientId`, `IdentityProviderId`), `Enabled`, `IsDefaultForClient`, `AutoRedirectIfSingle` (bool), `RequiredAcr` (nullable), `Order` (int).
  - Add table `IdentityProviderClaimMappings`
    - `Id` (PK), `IdentityProviderId` (FK), `ExternalClaim` (string), `LocalClaim` (string), `Transform` (nullable expression/enum), `Order`.
  - Add table `IdentityProviderKeys`
    - `Id` (PK), `IdentityProviderId` (FK), `Purpose` (enum: Signing, Encryption), `Jwk` (json), `Alg` (string), `Active` (bool), `CreatedAt`, `ExpiresAt` (nullable), `Kid`.
  - Optional: `ClientKeys` for inbound JAR verification (public keys/JWKS per client).
  - Acceptance: Migrations generated and applied on empty and existing DB; rollback supported.

- Story: OIDC provider config schema
  - Store in `IdentityProviders.ConfigJson` with validation:
    - `Authority`, `DiscoveryUrl` (optional override), `ClientId`, `ClientSecret` (or client assertion key ref), `ResponseType`, `Scopes` (list), `UsePKCE` (bool), `UseJAR` (bool, outbound), `UsePAR` (bool), `RequestedAcrValues` (string), `Prompt`, `ResponseMode`, `ClockSkewSeconds`, `TokenValidation` options (issuer/audience/expiry), `BackChannelLogout` (bool), `ExtraAuthParams` (kvp).
  - Acceptance: Invalid configurations rejected with actionable messages; `Authority` discovery validated on save if reachable.

2) Admin APIs and UI (Razor Pages in MrWhoOidc.WebAuth)
- Story: Management APIs (admin-only)
  - CRUD for `IdentityProvider`, `ClientIdentityProvider`, `IdentityProviderClaimMappings`, `IdentityProviderKeys`, and optional `ClientKeys`.
  - ProblemDetails for errors; model validation; RBAC policy.

- Story: Admin UI pages (Razor Pages)
  - Providers list/detail: create/edit OIDC provider, toggle enabled, upload/select logo, order, view discovery metadata, test connection.
  - Client ? Providers mapping: assign providers to client, set default, set order, toggle auto-redirect-if-single, set required ACR.
  - Claim mapping editor: map external ? local claims; built-in templates: `sub`, `email`, `name`, `preferred_username`, `roles`.
  - Keys page: view/import/rotate provider keys (for outbound JAR), manage client public keys (for inbound JAR). Support JWK/PEM import, mark active.
  - Acceptance: Full CRUD works, validation visible, audit notes recorded.

3) Authorization pipeline updates (IdP chaining)
- Story: Authorize endpoint parameterization
  - Accept custom `idp` and `idp_hint`; standard `login_hint`, `acr_values`.
  - Resolve client ? available providers. If 0: use local login (existing behavior). If 1 and `AutoRedirectIfSingle`: redirect. If >1 and no forced selection: render provider picker page.
  - Remember last used provider per client (cookie) if allowed by client config.
  - Respect `prompt=login`, `max_age`, `ui_locales` and propagate to upstream when applicable.
  - Acceptance: Routing logic tested across combinations.

- Story: External OIDC sign-in flow
  - Dynamically register external OIDC schemes per provider (cache discovery, refresh periodically; no app restart).
  - PKCE, nonce/state, correlation protections.
  - Callback handler: normalize/map claims, link/provision local subject, store upstream `iss+sub` linkage, then complete local authorization flow.
  - Handle error/cancel on upstream and allow re-selection.
  - Acceptance: Round-trip works with at least two OIDC providers.

4) Inbound JAR (clients ? WebAuth)
- Story: Request object parsing/validation
  - Support `request` and `request_uri` in authorize requests.
  - Validate JWT signature against client registered keys (`ClientKeys` or client JWKS), allowed `alg` set; enforce `aud`, `iss`, `exp`, `nbf` checks and replay protection (nonce/jti store, TTL).
  - Merge parameters per RFC 9101 precedence; reject conflicting parameters.
  - Acceptance: Conformance tests for valid/invalid signatures and claims.

- Story: Discovery metadata updates
  - `request_parameter_supported`, `request_uri_parameter_supported`, `request_object_signing_alg_values_supported`.
  - If PAR is added later: `pushed_authorization_request_endpoint`.
  - Acceptance: Well-known document validates with external tools.

5) Optional: Outbound JAR and PAR to upstream IdPs
- Story: Outbound JAR
  - If provider `UseJAR`, sign upstream auth request with a configured provider key; support at least `RS256`/`PS256` and `kid`.
  - Acceptance: Works against an upstream IdP requiring JAR.

- Story: Outbound PAR
  - If provider `UsePAR`, push request to upstream PAR endpoint, receive `request_uri`, then redirect using it.
  - Acceptance: Verified with an IdP enforcing PAR.

6) Token issuance and claims
- Story: Subject resolution and auto-provision
  - Link external user by `issuer+sub` pair.
  - Optional email-based linking with confirmation. Auto-provision toggle per client.
  - Acceptance: New and returning users handled without duplicates.

- Story: Claim mapping and propagation
  - Apply `IdentityProviderClaimMappings` to normalize claims; support transforms (copy, rename, regex, concat, case).
  - Add upstream info in our tokens: `idp`, `amr`, `acr`; propagate `auth_time`.
  - Acceptance: Downstream clients can consume upstream metadata.

7) Login UI changes (Razor Pages end-user flow)
- Story: Provider picker page
  - Display configured providers with logo, display name, description; keyboard/a11y friendly; mobile-friendly.
  - Respect default provider and auto-redirect-if-single; show “More options” if an IdP is hinted/remembered.
  - Acceptance: Works across themes/branding.

- Story: Error/edge cases
  - Friendly errors for upstream `access_denied`, `interaction_required`, `invalid_scope`, timeouts.
  - Allow re-selection upon cancel; preserve original authorize request state.
  - Acceptance: Tested with simulated failures.

8) Keys, crypto, and rotation
- Story: Key storage and rotation
  - Store provider keys (for outbound JAR) and client public keys (for inbound JAR). Support rotation and `kid`.
  - Background task to detect upcoming expiry; admin UI to activate/deactivate keys.
  - Acceptance: Rollover without downtime.

- Story: JWKS endpoints (if needed)
  - Optional public JWKS exposure per provider/client scope for interoperability.
  - Acceptance: JWKS fetch and cache behaviors verified.

9) Telemetry, security, resilience
- Story: Auditing & logging
  - Structured logs for provider selection, upstream start/finish, errors, claim mappings applied; correlation IDs.
  - Redact secrets; PII handling policy.
  - Acceptance: Logs useful for troubleshooting and pass security review.

- Story: Rate limiting & protections
  - Apply rate limits to authorize, callback, and JAR paths; CSRF protections on local UI; strict referrer policy.
  - Acceptance: Basic DoS protections in place.

10) Testing and documentation
- Story: Automated tests
  - Unit: config validation, claim mapping transforms, JAR parsing/validation.
  - Integration: multi-provider flow, picker UI, error recovery, discovery doc.
  - E2E: two upstream OIDC test providers (e.g., Azure AD, Auth0/Okta dev tenants).
  - Acceptance: CI green on .NET 9; critical paths covered.

- Story: Documentation
  - Admin guide for configuring providers and client mappings; examples for common IdPs.
  - Developer guide: using `idp`, `acr_values`, inbound JAR; discovery examples.
  - Acceptance: New client onboarding without code changes.

Rollout plan
- Phase 1: DB schema + read-only APIs + discovery updates (feature flags off).
- Phase 2: Admin CRUD + single upstream OIDC provider live.
- Phase 3: Multiple providers + picker UI + claim mapping.
- Phase 4: Inbound JAR.
- Phase 5: Optional outbound JAR/PAR.
- Phase 6: Hardening, audits, perf, docs.

Non-functional requirements
- Backwards compatibility when no providers configured.
- Secrets safety (Key Vault/DPAPI), no plaintext secrets at rest.
- Caching of discovery docs and keys; reasonable timeouts/retries.
- Observability and correlation across upstream/downstream requests.

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
```
