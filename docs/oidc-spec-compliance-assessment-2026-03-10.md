# OIDC Specification Compliance Assessment

Date: 2026-03-10

Scope: current MrWhoOidc OpenID Provider implementation, with emphasis on OpenID Connect Core 1.0, Discovery 1.0, Dynamic Client Registration 1.0 / Management 1.0, Session Management 1.0, RP-Initiated Logout 1.0, and CIBA where advertised.

## Executive Summary

The implementation is strong on the core OP surface: discovery, JWKS, authorization code flow, userinfo, logout, pairwise subject identifiers, PAR/JAR/JARM, DPoP, token exchange, and several advanced features are clearly present. The main compliance risks are not missing endpoints; they are mismatches between what the server advertises or accepts and what it actually enforces end to end.

The most important confirmed weaknesses are:

1. The authorize request pipeline drops several OIDC parameters before validation and issuance logic sees them.
2. Dynamic client registration accepts and echoes a broad set of OIDC client metadata that is not validated, persisted, or enforced consistently.
3. The CIBA implementation is functional but looser than the spec expects in a few places, especially around hint-token validation and delivery-mode semantics.

These are fixable. The main recommendation is to tighten truthfulness: if metadata or parameters are advertised as supported, they need to survive the full request path and affect runtime behavior.

## Confirmed Strengths

- Discovery metadata is broadly implemented and tenant-aware in `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`.
- Pairwise subject identifiers are not just advertised; they are implemented in `MrWhoOidc.Auth/Services/SubjectIdentifiers/PairwiseSubjectService.cs` and covered by tests in `MrWhoOidc.UnitTests/Integration/PairwiseSubjectIdentifiersTests.cs`.
- UserInfo enforces access-token shape (`typ=at+jwt`), validates audience conservatively, and supports DPoP-bound access tokens in `MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs`.
- Authorization code issuance persists nonce, resource, PKCE, and claims JSON when the validated request contains them in `MrWhoOidc.Auth/Services/AuthorizationCodeService.cs`.
- RP-initiated logout, front-channel logout, and back-channel logout are implemented with explicit handlers rather than stubs.

## Findings

### 1. High: `/authorize` drops core OIDC parameters before validation

Severity: High

Why this matters:

OpenID Connect support depends on the authorization request carrying parameters such as `claims`, `max_age`, `acr_values`, `display`, `ui_locales`, `login_hint`, and `id_token_hint` all the way through request resolution, validation, login UX, consent, and authorization-code persistence. In the current code, several of those parameters are defined in the request model and validated in the validator, but they are dropped during request mapping.

Evidence:

- `MrWhoOidc.Auth/Services/Authorization/AuthorizeRequest.cs` defines fields for `prompt`, `max_age`, `id_token_hint`, `login_hint`, `acr_values`, `display`, `ui_locales`, and `claims`.
- `MrWhoOidc.Auth/Services/AuthorizeRequestResolver.cs` maps query input into `AuthorizeRequest`, but `MapQueryToRequest` only populates `response_type`, `client_id`, `redirect_uri`, `scope`, `state`, `nonce`, `code_challenge`, `code_challenge_method`, `resource`, and `response_mode`.
- `MrWhoOidc.WebAuth/Services/AuthorizeRequestOrchestrator.cs` then rebuilds a new `AuthorizeRequest` and again only copies the reduced field set.
- `MrWhoOidc.Auth/Services/Authorization/AuthorizeRequestValidator.cs` contains validation logic for `prompt`, `max_age`, `acr_values`, and `claims`, but that logic only works if those values reach the validator.

Practical impact:

- `claims` from `/authorize` will not be normalized and persisted with the authorization code, even though discovery advertises `claims_parameter_supported=true`.
- `max_age` and `acr_values` support is unreliable because the validator path never receives those parameters from the resolved authorize request.
- `display=popup` and `ui_locales` do not survive into the login redirect path reliably because `domainReq.display` is populated from the truncated model.
- `login_hint` and `id_token_hint` do not propagate through the standard authorize request model.
- This affects direct query requests and also JAR/PAR, because the resolver collapses the resolved request to the reduced shape.

Recommended update:

- Fix the root cause in `AuthorizeRequestResolver.MapQueryToRequest` and `AuthorizeRequestOrchestrator.ResolveAndValidateAsync` so they preserve the full `AuthorizeRequest` shape.
- Add integration tests that prove these parameters survive across plain authorize requests, JAR, and PAR.
- Only advertise `display_values_supported`, `ui_locales_supported`, `claims_parameter_supported`, and `acr_values_supported` when the full request path actually enforces them.

Suggested test additions:

- `/authorize` with `claims` persists `ClaimsJson` into the issued authorization code.
- `/authorize` with `max_age` reaches validator and triggers re-authentication logic.
- `/authorize` with `acr_values` reaches validator and login enforcement.
- `/authorize` with `display=popup` propagates into the login redirect.
- PAR and JAR variants of the same cases.

### 2. High: dynamic client registration accepts metadata that is not implemented end to end

Severity: High

Why this matters:

OIDC dynamic registration is only interoperable when the server either implements client metadata semantics or rejects unsupported metadata with `invalid_client_metadata`. Accepting metadata and echoing it back while ignoring it at runtime is a compliance and interoperability problem.

Evidence:

- `MrWhoOidc.WebAuth/Models/DynamicRegistration/ClientRegistrationRequest.cs` and `ClientRegistrationResponse.cs` expose a wide metadata surface including:
  - `software_statement`
  - `request_object_signing_alg`
  - `request_object_encryption_alg`
  - `request_object_encryption_enc`
  - `default_max_age`
  - `require_auth_time`
  - `default_acr_values`
  - `initiate_login_uri`
  - `request_uris`
- `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs` returns these values in the registration response.
- `MapRequestToClient` in the same file persists only a subset of the request metadata. The fields above are not stored on the `Client` entity there.
- `MrWhoOidc.WebAuth/Handlers/ClientConfigurationHandler.cs` updates only a smaller subset again and does not validate or manage that broader metadata set.
- Grep across the repository shows almost all of the fields above exist only in the request/response models and the registration handler response path, not in runtime enforcement code.

Practical impact:

- A client can register metadata successfully and receive it back in the `201` response, but the OP will silently discard it.
- `default_max_age`, `require_auth_time`, and `default_acr_values` cannot influence authorization behavior if they are not persisted and consulted.
- `request_uris` and `initiate_login_uri` are declared but effectively unsupported.
- `software_statement` is modeled but not validated. Additionally, `software_statement` is accepted in the request but is **not echoed** in the `201` response, unlike the other unsupported fields — making it inconsistent even within the echo-back behavior.
- `GET /register/{client_id}` does not return any of these fields because they were never persisted — so the metadata is lost immediately after the initial registration response.
- This will create confusing RP behavior and conformance failures, especially for automated DCR clients.

Recommended update:

- Decide field by field whether the server truly supports the metadata.
- For supported metadata: persist it, validate it, and enforce it at runtime.
- For unsupported metadata: reject it explicitly with `invalid_client_metadata` rather than accepting and echoing it.
- Apply the same rule to `PUT /register/{client_id}` so update semantics match create semantics.

Priority metadata to fix first:

1. `default_max_age`
2. `require_auth_time`
3. `default_acr_values`
4. `request_uris`
5. `software_statement`
6. request object crypto metadata

### 3. Medium: CIBA validation is present but not strict enough for a spec-facing feature

Severity: Medium

Why this matters:

The discovery document can advertise CIBA support. Once advertised, RPs expect stricter behavior around hint-token validation, delivery-mode rules, and client metadata semantics.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/CibaAuthenticationHandler.cs` supports `login_hint`, `login_hint_token`, and `id_token_hint`, and issues `auth_req_id` responses.
- `ValidateLoginHintTokenAsync` validates signatures only against `client.PublicJwksJson`; it does not use `jwks_uri` and it sets `ValidateIssuer=false`.
- `TokenHasExpectedAudience` returns `true` when the token has no audience, which is permissive for a backchannel hint token targeted at the OP.
- Delivery mode is inferred by `DetermineDeliveryMode` from global configuration plus presence of `client_notification_token`; there is no visible per-client delivery-mode contract.
- `IsValidClientNotificationToken` is syntax checking only (length ≤ 1024, no control/whitespace characters). Note: syntax-only validation is acceptable per CIBA spec section 7.1 for **poll** mode, but insufficient for **push** and **ping** modes where the notification token has higher trust requirements because the OP uses it to call back to the client.

Practical impact:

- `login_hint_token` acceptance is weaker than expected for a high-trust authentication initiation mechanism.
- Clients using `jwks_uri` without inline `jwks` are not clearly supported for this path.
- Delivery-mode behavior may diverge from client expectations if the OP effectively chooses the mode at runtime.
- For push/ping delivery modes, the lack of semantic validation on `client_notification_token` could allow an RP to provide a weak or replayable token that the OP then trusts for callback authentication.

Recommended update:

- Tighten `login_hint_token` validation to require explicit audience to this OP and apply stricter issuer/signing constraints.
- Support client signing keys from `jwks_uri` where that is the registered key source, or reject that registration shape for CIBA clients.
- Make delivery mode a client-level capability/registration decision rather than a runtime inference from token presence.
- Add negative integration tests for missing audience, wrong audience, missing signing keys, and unsupported delivery mode combinations.

### 4. Medium: dynamic registration does not validate metadata consistency strongly enough

Severity: Medium

Why this matters:

Several OIDC and OAuth client metadata combinations should be rejected when inconsistent rather than accepted leniently.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs` validates redirect URIs, grant types, response types, auth method, application type, subject type, and some ID token crypto metadata.
- It does not clearly reject `jwks` plus `jwks_uri` combinations.
- It does not validate `sector_identifier_uri` at registration time, despite the codebase having sector identifier validation services under `MrWhoOidc.Auth/Services/SubjectIdentifiers`.
- It does not validate unsupported OIDC metadata such as `software_statement` and request-object metadata; it effectively accepts them.

Practical impact:

- The DCR endpoint is friendlier than the spec should allow, but the cost is post-registration ambiguity and weaker interoperability.

Recommended update:

- Enforce mutual exclusivity and completeness rules for `jwks` / `jwks_uri`.
- Validate pairwise clients with `sector_identifier_uri` using the existing subject-identifier validation services.
- Reject unknown or unimplemented OIDC client metadata instead of storing or echoing it passively.

### 5. Medium: `display` and `ui_locales` are advertised more confidently than they are implemented

Severity: Medium

Why this matters:

Discovery currently advertises `display_values_supported` and optionally `ui_locales_supported`. Those values should reflect effective support at the authorization UX layer.

Evidence:

- `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` publishes `display_values_supported` and `ui_locales_supported`.
- `MrWhoOidc.WebAuth/Services/AuthenticationRedirectService.cs` only preserves `popup` as a special case.
- Because the authorize mapping currently drops `display` and `ui_locales`, support is not end-to-end.

Practical impact:

- RPs may use discovery metadata to drive popup UX or locale behavior that does not actually work.

Recommended update:

- Fix the authorize request propagation first.
- After that, either implement locale propagation fully in the login/consent UI or stop advertising it.
- Keep `display_values_supported` limited to values that materially change UI behavior.

## Recommended Remediation Plan

### Priority 0: fix authorize parameter propagation

- Update `MrWhoOidc.Auth/Services/AuthorizeRequestResolver.cs` to map all OIDC parameters already present in `AuthorizeRequest`.
- Update `MrWhoOidc.WebAuth/Services/AuthorizeRequestOrchestrator.cs` to pass through the resolved `AuthorizeRequest` instead of reconstructing a reduced one.
- Add regression tests for `claims`, `max_age`, `acr_values`, `display`, and `ui_locales`.

### Priority 1: make DCR truthful

- Inventory every field in `ClientRegistrationRequest`.
- For each field, choose one of two outcomes:
  - persist + validate + enforce
  - reject with `invalid_client_metadata`
- Apply the same decision on create, read, and update paths.

### Priority 2: harden CIBA

- Require stricter `login_hint_token` audience semantics.
- Support `jwks_uri` for client signing keys or reject that registration mode for CIBA.
- Make delivery mode client-driven and test each supported mode explicitly.

### Priority 3: align discovery with actual behavior

- Re-review every advertised capability in `DiscoveryHandler.cs` after the fixes above.
- Remove or gate metadata that is only partially implemented.

## Suggested Test Additions

- Authorization integration tests for query, JAR, and PAR carrying `claims`, `max_age`, `acr_values`, `display`, and `ui_locales`.
- Dynamic registration tests that verify unsupported metadata is rejected rather than echoed.
- Dynamic registration tests for `sector_identifier_uri`, `jwks`/`jwks_uri`, and request-object metadata validation.
- CIBA negative tests for invalid `login_hint_token` issuer/audience/key source combinations.

## Overall Assessment

This is a capable OIDC Provider with substantial feature depth. The main compliance problem is not the absence of protocol code; it is drift between the public contract and the runtime contract. Fixing authorize parameter propagation and making dynamic registration truthful will materially improve spec compliance and interoperability without requiring a redesign of the platform.

## Reviewer Notes (2026-03-10)

All five findings were independently verified against the codebase. Every cited file, method, and behavioral claim was confirmed accurate.

### Verification Summary

| Finding | Claim Verified | Notes |
|---------|---------------|-------|
| 1. Authorize param propagation | **Yes** | `MapQueryToRequest` constructs with 10/18 fields; orchestrator rebuilds with same 10. Validator logic for `prompt`, `max_age`, `acr_values`, `claims` is effectively dead code. |
| 2. DCR metadata enforcement | **Yes** | All 9 fields confirmed present in models, absent from `Client` entity and `MapRequestToClient`. Additional gap: `software_statement` not echoed in response. |
| 3. CIBA validation strictness | **Yes** | `ValidateIssuer=false`, audience-less tokens accepted, delivery mode is global-only, notification token is syntax-only. |
| 4. DCR consistency validation | **Yes** | `jwks` + `jwks_uri` both accepted simultaneously. `sector_identifier_uri` stored without validation despite existing services. |
| 5. display/ui_locales support | **Yes** | Discovery advertises both. `NormalizeDisplay()` discards `"page"` (returns null). `ui_locales` has no implementation in the redirect or login path. |

### Additional Findings Not in Original Assessment

1. **`software_statement` echo gap**: The registration response echoes all other unsupported metadata fields back to the client, but `software_statement` is silently dropped from the response. This is an additional inconsistency within the already-problematic echo behavior.

2. **CIBA `client_notification_token` mode sensitivity**: The syntax-only validation of this token is spec-compliant for poll mode (CIBA section 7.1) but insufficient for push/ping modes. The fix should be mode-aware: tighten validation when the resolved delivery mode is push or ping.

3. **`display="page"` silently ignored**: Discovery advertises `display_values_supported: ["page", "popup"]`, but `NormalizeDisplay()` only returns a value for `"popup"` — `"page"` maps to `null`, making it indistinguishable from no `display` parameter at all. Either remove `"page"` from discovery or implement it as the explicit default.

### Assessment of Remediation Plan

The four-priority ordering is sound:

- **P0 (authorize params)** is correctly identified as the root cause — it blocks multiple OIDC features and makes existing validator code unreachable.
- **P1 (DCR truthfulness)** is the right second step because it affects interoperability for any automated RP.
- **P2 (CIBA hardening)** correctly follows DCR since some CIBA fixes interact with client registration metadata (delivery mode, key source).
- **P3 (discovery alignment)** must be last because it depends on what P0–P2 actually implement.

The proposed fixes are surgical — they do not require architectural changes. The existing validator, entity model, and service infrastructure can absorb all of the recommended changes.