# OIDC Specification Compliance Assessment

Date: 2026-03-10

Scope: current MrWhoOidc OpenID Provider implementation, with emphasis on OpenID Connect Core 1.0, Discovery 1.0, Dynamic Client Registration 1.0 / Management 1.0, Session Management 1.0, RP-Initiated Logout 1.0, Front-Channel Logout 1.0, Back-Channel Logout 1.0, Device Authorization Grant, PAR, JAR, JARM, DPoP, mTLS, and CIBA where advertised.

## Executive Summary

MrWhoOidc is a capable OpenID Provider with strong coverage of the core OIDC surface and several advanced extensions. The implementation is materially stronger than some older repository documents suggest: the earlier authorize-parameter propagation gap has been fixed, pairwise subject identifiers are implemented, and dynamic client registration is stricter than before.

The main remaining compliance risks are now concentrated in three areas:

1. Dynamic client registration advertises and accepts metadata that is not round-trippable or enforceable end to end.
2. `jwks_uri` is accepted and persisted, but several runtime paths still only use inline `jwks` / `PublicJwksJson`.
3. A small number of advertised behaviors remain partially enforced, especially `require_auth_time` and parts of CIBA validation.

This is no longer a server with missing basic endpoints. It is a server that needs tighter contract fidelity between registration, discovery, and runtime behavior.

## Confirmed Strengths

- Core OP endpoints are implemented: authorization, token, userinfo, discovery, JWKS, revocation, and introspection.
- Authorization code flow is enforced with PKCE S256 where required.
- PAR, JAR, and JARM are implemented with request validation and replay protection.
- DPoP support is present, including replay defenses and userinfo enforcement.
- Device Authorization Grant is implemented.
- CIBA is implemented with poll and callback-oriented modes.
- Pairwise subject identifiers are implemented in `MrWhoOidc.Auth/Services/SubjectIdentifiers/PairwiseSubjectService.cs`.
- RP-initiated logout, front-channel logout, back-channel logout, and `check_session_iframe` are implemented.
- UserInfo signed and encrypted JWT responses are implemented.
- Dynamic client registration rejects several unsupported optional metadata fields rather than silently echoing them.

## Current Findings

### 1. High: `jwks_uri` is accepted by registration but ignored by several runtime validation paths

Why this matters:

OIDC and OAuth clients commonly register `jwks_uri` instead of embedding `jwks`. If the OP accepts `jwks_uri`, stores it, and returns it from registration, clients reasonably expect it to work for key-based features.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs` accepts `jwks_uri` and stores it in `PublicJwksUri`.
- `MrWhoOidc.WebAuth/Handlers/ClientConfigurationHandler.cs` also persists and returns `JwksUri`.
- `MrWhoOidc.Auth/Services/ClientAssertionValidator.cs` validates `private_key_jwt` using `client.PublicJwksJson` only and explicitly notes that fetching from `jwks_uri` is not implemented.
- `MrWhoOidc.Auth/Services/RequestObjectValidator.cs` validates JAR signatures using `client.PublicJwksJson` only.
- `MrWhoOidc.WebAuth/Handlers/CibaAuthenticationHandler.cs` validates `login_hint_token` using `client.PublicJwksJson` only.
- `MrWhoOidc.Auth/Services/Token/AuthorizationCodeExchanger.cs` and `MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs` derive encryption keys from `PublicJwksJson`, not `PublicJwksUri`.

Practical impact:

- A dynamically registered client that provides only `jwks_uri` can be accepted by the OP but still fail later when using `private_key_jwt`, JAR, CIBA `login_hint_token`, or encrypted response features.
- This is a contract mismatch between registration and runtime.

Recommended update:

- Introduce a shared client-key resolver that supports both inline `jwks` and `jwks_uri`.
- Use that resolver consistently in `ClientAssertionValidator`, `RequestObjectValidator`, `CibaAuthenticationHandler`, `AuthorizationCodeExchanger`, and `UserInfoHandler`.
- If `jwks_uri` support is intentionally out of scope, reject `jwks_uri` in registration instead of accepting it.

### 2. High: Dynamic client registration management is not round-trippable

Why this matters:

RFC 7592 management operations are expected to expose the current client metadata, not a reduced subset. A client should be able to register metadata, read it back, and update it consistently.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs` returns a broad `ClientRegistrationResponse` on `POST /register`, including `token_endpoint_auth_method`, `grant_types`, `response_types`, `client_uri`, `logo_uri`, `scope`, `contacts`, `tos_uri`, `policy_uri`, `software_id`, and `software_version`.
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` does not persist many of those fields on the `Client` entity.
- `MrWhoOidc.WebAuth/Handlers/ClientConfigurationHandler.cs` `BuildClientResponse` returns only a narrower subset of metadata.
- `MrWhoOidc.WebAuth/Handlers/ClientConfigurationHandler.cs` `UpdateClientAsync` updates only that smaller subset and does not manage `token_endpoint_auth_method`, `grant_types`, `response_types`, `client_uri`, `logo_uri`, `scope`, `contacts`, `tos_uri`, `policy_uri`, `software_id`, or `software_version`.

Practical impact:

- `POST /register` may appear successful for metadata that `GET /register/{client_id}` cannot return later.
- `PUT /register/{client_id}` cannot fully manage metadata previously accepted on create.
- Interoperability with automated registration clients is weaker than the create response suggests.

Recommended update:

- Decide which metadata is truly supported for long-term storage and management.
- For supported metadata: persist it, return it from `GET`, and allow coherent update behavior via `PUT`.
- For unsupported metadata: stop echoing it on `POST` and reject it with `invalid_client_metadata`.
- Add round-trip tests: create, read, update, read again.

### 3. Medium: `require_auth_time` is accepted and persisted but not enforced

Why this matters:

If a client registers `require_auth_time=true`, the ID token should reliably include `auth_time` for that client.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs` accepts and persists `require_auth_time`.
- `MrWhoOidc.WebAuth/Handlers/ClientConfigurationHandler.cs` returns `RequireAuthTime` and updates it.
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` stores `RequireAuthTime` on `Client`.
- `MrWhoOidc.Auth/Services/Token/AuthorizationCodeExchanger.cs` computes `authTimeForIdToken` from the authorization context but does not consult `client.RequireAuthTime` anywhere.
- Repository tests under `MrWhoOidc.UnitTests/DynamicClientRegistrationTests.cs` verify persistence and echoing of `RequireAuthTime`, but not ID token enforcement.

Practical impact:

- A client can successfully negotiate `require_auth_time=true` without a guarantee that issued ID tokens will satisfy that metadata.

Recommended update:

- During ID token issuance, require `auth_time` whenever `client.RequireAuthTime == true`.
- If the OP cannot determine an authentication time for that authorization, fail the token exchange with a protocol error instead of emitting a non-conformant ID token.
- Add focused tests for `require_auth_time=true` with and without available `auth_time`.

### 4. Medium: CIBA hint-token validation is still looser than the advertised feature depth suggests

Why this matters:

CIBA is a high-trust feature. Once advertised in discovery, relying parties expect strict validation of hint tokens and stable delivery-mode semantics.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/CibaAuthenticationHandler.cs` validates `login_hint_token` audience, but sets `ValidateIssuer = false`.
- The same handler derives signing keys from `client.PublicJwksJson` only, not `PublicJwksUri`.
- `DetermineDeliveryMode` derives the mode from global configuration plus token presence rather than a clearly persisted per-client contract.
- `IsValidClientNotificationToken` performs syntax-only validation.

Practical impact:

- `login_hint_token` acceptance is still more permissive than ideal.
- Clients using only `jwks_uri` may be accepted at registration but fail on CIBA hint-token validation.
- Delivery-mode behavior is more implementation-defined than client-negotiated.

Recommended update:

- Validate `login_hint_token` issuer explicitly when the deployment has a defined trust model.
- Reuse the same shared client-key resolver described in Finding 1.
- Persist or derive CIBA delivery mode from client metadata rather than only from global configuration and token presence.
- Tighten `client_notification_token` checks for push/ping modes.

### 5. Medium: `sector_identifier_uri` is only partially validated at registration time

Why this matters:

For pairwise subject clients, bad `sector_identifier_uri` metadata should ideally be rejected during registration rather than discovered later during live token issuance.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs` checks only that `sector_identifier_uri` is HTTPS when supplied for pairwise clients.
- `MrWhoOidc.Auth/Services/SubjectIdentifiers/SectorIdentifierUriValidator.cs` contains a stronger validator that fetches the document and verifies the registered redirect URIs.
- That stronger validation is invoked by `MrWhoOidc.Auth/Services/SubjectIdentifiers/SectorIdentifierResolver.cs` at runtime, during pairwise subject resolution.

Practical impact:

- A client can be registered successfully with a syntactically valid but operationally invalid `sector_identifier_uri`.
- The failure may then surface later during authorization or token issuance rather than at registration time.

Recommended update:

- Reuse `SectorIdentifierUriValidator` during registration and client configuration update flows.
- Fail early with `invalid_client_metadata` when the fetched `sector_identifier_uri` document does not match the registered redirect URIs.

### 6. Low: repository conformance checks are drifting from actual discovery behavior

Why this matters:

This is not an OP protocol defect by itself, but it makes the repository a weaker source of truth for compliance.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` sets `tls_client_certificate_bound_access_tokens` to `false`, with an explicit comment that mTLS is optional rather than universal.
- `MrWhoOidc.UnitTests/Integration/DiscoveryMetadataTests.cs` still expects `tls_client_certificate_bound_access_tokens` to be `true`.

Practical impact:

- The test suite is asserting a stronger discovery contract than the server actually publishes.
- That makes compliance documentation and regression confidence less reliable.

Recommended update:

- Align the test with the implemented discovery contract, or change the implementation if universal certificate-bound tokens are actually intended.

## Missing Optional Features

These are not current compliance defects because the server rejects or does not advertise them, but they remain feature gaps relative to the broader OIDC ecosystem:

- Third-Party-Initiated Login via `initiate_login_uri` is not supported.
- Registered `request_uris` metadata is not supported.
- `software_statement` is not supported.
- Request object client metadata (`request_object_signing_alg`, `request_object_encryption_alg`, `request_object_encryption_enc`) is not supported for DCR.
- Implicit and hybrid response types are not supported; the OP is code-flow only.
- Local UI locale handling for `ui_locales` appears incomplete; the parameter survives request resolution and is forwarded to some external-IdP paths, but there is no clear local login/consent localization pipeline using it.

These are reasonable product choices as long as discovery and registration continue to be truthful.

## Recommended Remediation Plan

### Priority 0: make key source handling truthful

- Implement a shared runtime resolver for client keys that supports both inline `jwks` and `jwks_uri`.
- Apply it across `private_key_jwt`, JAR, CIBA `login_hint_token`, and encrypted response features.
- If remote JWKS fetch is not desired, reject `jwks_uri` at registration.

### Priority 1: repair RFC 7592 round-tripping

- Inventory the metadata returned on `POST /register`.
- For each field, choose one of two outcomes:
  - persist + return + update
  - reject on create/update
- Add round-trip integration tests covering `POST`, `GET`, and `PUT`.

### Priority 2: enforce negotiated client metadata

- Enforce `require_auth_time` in ID token issuance.
- Reuse full `sector_identifier_uri` validation during registration, not only at runtime.

### Priority 3: tighten CIBA semantics

- Harden `login_hint_token` issuer validation.
- Make delivery-mode behavior explicitly client-facing and testable.
- Add negative tests for unsupported key source combinations and invalid hint tokens.

### Priority 4: align tests and docs with reality

- Update stale assessment documents that still refer to already-fixed gaps.
- Fix the discovery integration test around `tls_client_certificate_bound_access_tokens`.
- Add tests for DCR management round-tripping and `require_auth_time` issuance behavior.

## Overall Assessment

This OP is broadly compliant on the core OIDC surface and stronger than many custom implementations in feature depth. The main remaining problems are now about truthfulness and lifecycle consistency rather than missing protocol endpoints. Fixing `jwks_uri` handling, RFC 7592 round-tripping, and `require_auth_time` enforcement would materially improve interoperability and reduce the highest remaining compliance risk.