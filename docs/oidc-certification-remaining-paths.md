# Remaining OpenID Certification Paths

Status as of 2026-11-14.

This document records the certification paths that are already covered, the remaining
OpenID Connect provider paths that are appropriate for MrWhoOidc, and the optional FAPI
paths that require a separate gap assessment.

This is a test and submission plan, not evidence of certification. The 2026-11-13
hosted round was self-certification testing; no package has been submitted to the OpenID
Foundation.

## Baselines

- Evidence baseline: `da7f8af72f43193e29622b1e999d09121dfce8fa` (2026-11-13 hosted round).
- Code baseline for new work: `581f55cb` (post-round hardening, not yet deployed as the
  certification target).
- Existing packages are archived under `tools/certification/.generated/exports/`.
- Do not mix packages produced from different commits or deployments in one submission.

## Already Covered

| Profile | Plan | 2026-11-13 result |
| --- | --- | --- |
| Config OP | `oidcc-config-certification-test-plan` | 1 PASSED |
| Basic OP | `oidcc-basic-certification-test-plan` | 28 PASSED, 3 REVIEW, 7 SKIPPED |
| Form Post OP | `oidcc-formpost-basic-certification-test-plan` | 28 PASSED, 3 REVIEW, 7 SKIPPED |
| RP-Initiated Logout OP | `oidcc-rp-initiated-logout-certification-test-plan` | 3 PASSED, 8 REVIEW |
| Front-Channel Logout OP | `oidcc-frontchannel-rp-initiated-logout-certification-test-plan` | 2 PASSED |
| Back-Channel Logout OP | `oidcc-backchannel-rp-initiated-logout-certification-test-plan` | 2 PASSED |

The logout submission rule is already satisfied: RP-Initiated Logout plus at least one of
front-channel or back-channel logout.

## Remaining Viable Paths

| Priority | Profile | Plan | Readiness | Notes |
| --- | --- | --- | --- | --- |
| 1 | Dynamic OP | `oidcc-dynamic-certification-test-plan` | Run and verify | Mostly implemented; DCR must be available for the run |
| 2 | Session Management OP | `oidcc-session-management-certification-test-plan` | Likely pass; manual evidence | Requires a real browser for the `check_session_iframe` module |
| 3 | 3rd Party-Initiated Login OP | `oidcc-3rdparty-init-login-certification-test-plan` | Small feature gap | DCR currently rejects `initiate_login_uri` |
| 4 | FAPI 2.0 Security Profile Final | `fapi2-security-profile-final-test-plan` | Conditional gap assessment | Closest FAPI fit; start with the mTLS variant |
| 5 | FAPI 1.0 Advanced Final | `fapi1-advanced-final-test-plan` | Conditional gap assessment | Legacy profile; only with a business driver |
| 6 | FAPI-CIBA | `fapi-ciba-id1-test-plan` | Conditional gap assessment | CIBA exists; FAPI-specific behavior is unverified |
| 7 | FAPI 2.0 Message Signing | `fapi2-message-signing-final-test-plan` | Optional stretch | JAR/JARM exists; do not treat as ready |

### 1. Dynamic OP

Plan: `oidcc-dynamic-certification-test-plan`

Why it fits:

- Dynamic client registration and RFC 7592 client management are implemented.
- `private_key_jwt`, `jwks_uri`, `sector_identifier_uri`, `request_uris`, and the
  registration metadata exercised by the plan are supported.
- Refresh tokens and signing-key rotation exist.

Prerequisites and caveats:

- The plan uses dynamic client registration. Prefer the seeded initial access token if
  the runner supports it. Otherwise temporarily set
  `Dcr__AllowAnonymousInProduction=true` for the run and set it back to `false`
  afterward.
- The OP key-rotation module expects an on-demand rotation or a self-attestation. The
  background rotation service exists, and a bootstrap-token-guarded on-demand rotation
  endpoint is used by `tools/certification/repair-rotate-keys.py` to repair the module
  in-plan after the auto-started instance fails.
- Run against the exact commit that will become the certification target.
- **OIDCD-3 profile detail (learned during the run): the plan's discovery/response-type
  set comparison and the `oidcc-userinfo-rs256` module require the implicit flow to be
  implemented** — `response_types_supported` must contain `id_token` and
  `id_token token`, `grant_types_supported` must contain `implicit`, and a client
  registered with `userinfo_signed_response_alg=RS256` must receive a signed
  `application/jwt` userinfo response. All implemented (2026-11-15).
- RFC 7523 §2.2: the suite omits `client_id` when authenticating with a JWT client
  assertion; the OP must derive it from the assertion's `iss`/`sub` (implemented).
- RFC 9126 §5.1: `exp` on a request object is RECOMMENDED, not required; the suite's
  signed request_uri test omits it (accepted since 2026-11-15).
- `oidcc-registration-logo-uri` / `oidcc-registration-policy-uri` /
  `oidcc-registration-tos-uri` cannot run with the scripted-browser automation: the
  suite races ("runInBackground called after runFinalisationTaskInBackground()").
  Run them with a plan-level `override` that replaces their `browser` config with an
  empty array, then upload a login-page screenshot to the module's image placeholder
  via `POST /api/log/{id}/images/{placeholder}`; they finish cleanly with REVIEW.
- `oidcc-server-rotate-keys` always fails as auto-started (the runner cannot trigger a
  rotation); repair it in-plan afterwards with `tools/certification/repair-rotate-keys.py`.

Status: **local round passed 2026-11-15** — plan `9bZZT9xLeF6gR`
(alias `mrwhooidc-tunnel-dyn4`, local Docker stack via cloudflared quick tunnel):
14 PASSED, 6 REVIEW (screenshot evidence embedded in the plan export), 3 SKIPPED
(expected: `oidcc-idtoken-unsigned`, `oidcc-request-uri-unsigned`,
`oidcc-ensure-request-object-with-redirect-uri`), 0 FAILED/WARNING/INTERRUPTED.
Evidence: `tools/certification/.generated/exports/` (plan export, plan JSON, runner
log, evidence index). The certification package (publish + immutable) is created from
the public deployment once the fixes are committed and deployed there.

Exit criteria: all modules PASSED/REVIEW/SKIPPED as expected, no unexpected FAILURE or
WARNING, package exported and indexed. ✓ (local round; public round pending)

### 2. Session Management OP

Plan: `oidcc-session-management-certification-test-plan`

Why it fits:

- `check_session_iframe` and `end_session_endpoint` are implemented and advertised.
- The existing logout work covers the RP-initiated part of the flow.

Work done for the local round (all changes on top of `9eb1506d`, uncommitted):

- The `check_session_iframe` script previously used `fetch()`, `crypto.subtle` and
  `async/await`, none of which the suite's scripted browser (HtmlUnit) can execute.
  The `session_state` comparison moved server-side: the iframe relays the RP's
  message (`client_id + " " + session_state`) and its origin to
  `/connect/checksession/state?received_message=…&origin=…` via a **synchronous**
  `XMLHttpRequest` and posts the reply with `postMessage`. The whole check completes
  inside one JavaScript job, so it survives the scripted browser closing the page
  soon after it loads. New `SessionStateCalculator` (WebAuth.Services) centralizes
  the `hash.salt` computation used by both the authorize response and the state
  endpoint; `CheckSessionStateHandler.CompareAsync` performs the server-side
  comparison (liveness first — a revoked/expired/idle session answers `changed`
  whatever the hash says — then the hash, binding client_id, origin and browser
  state). 18 unit tests cover the comparison matrix.
- Browser automation for the suite's `session_verify`/`second_session_verify` pages
  needs a `*session_verify*` match with a `["wait","contains","session_result",20]`
  task: the suite's WebRunner closes its HtmlUnit driver as soon as the page is
  loaded, which would kill the RP iframe's 500 ms timer before the postMessage
  round-trip. The pattern must not start with a slash — `*/session_verify*` does not
  match `…/second_session_verify`.
- Pre-suite verification with a real browser (Playwright Chromium through the
  tunnel, `/tmp/opencode/session-mgmt-check.py`): DCR with
  `post_logout_redirect_uris`, 128-char state authorize flow, token exchange, first
  check `unchanged`, `end_session` redirect carrying only `state`, second check
  `changed`.

Local round result (plan `AXbS62CPeUnu6`, alias `mrwhooidc-sm`, tunnel issuer):

- `oidcc-session-management-discovery-endpoint-verification` PASSED
- `oidcc-session-management-rp-initiated-logout` PASSED — full browser-driven chain:
  `session_state` in the authorization response, first check `unchanged`,
  post-logout redirect with exactly `state`, second check `changed` (the server-side
  OP session is revoked on logout). 0 failures, 0 warnings.
- Evidence: `oidcc-session-management-certification-test-plan-code-dynamic_client-AXbS62CPeUnu6-15-Nov-2026.zip`,
  `session-management-op-plan-AXbS62CPeUnu6.json`, `session-management-op-local-run-1.log`,
  plus the evidence-index section.

Exit criteria: both modules completed with expected results and manual evidence attached. ✓ (local round; public round + package pending)

### 3. 3rd Party-Initiated Login OP

Plan: `oidcc-3rdparty-init-login-certification-test-plan`

Status: **no code changes were needed.** The "current gap" listed earlier was stale —
`initiate_login_uri` was already fully implemented on top of `9eb1506d`:

- Storage: `Client.InitiateLoginUri` column + migration
  `20261115071625_AddRequestObjectSigningAlgAndInitiateLoginUri`.
- DCR (`POST /register`) and RFC 7592 client configuration (`GET`/`PUT
  /register/{client_id}`) both accept the field and echo it in their responses.
- `DynamicClientMetadataValidator.ValidateInitiateLoginUri` requires an absolute
  HTTPS URI; non-HTTPS values are rejected with 400 `invalid_client_metadata`.
- Unit tests cover HTTPS acceptance, non-HTTPS rejection, and round-tripping.

The plan only checks registration/configuration behavior and non-HTTPS rejection; it
does not drive a complete initiate-login flow.

Local round result (plan `oIuR7NYEowEAg`, alias `mrwhooidc-3pi`, tunnel issuer):

- `oidcc-3rd_party-init-login` PASSED — DCR with `initiate_login_uri`, echoed correctly
  in the registration response and the RFC 7592 client-configuration response
  (instance `fuoQvUOzox1DdNA`). 0 failures, 0 warnings.
- `oidcc-3rd_party-init-login-nohttps` PASSED — DCR with an `http://` variant of the
  URI rejected with 400 + `invalid_client_metadata` (instance `7hhOZJRCMV0VbQ8`).
  0 failures, 0 warnings.
- Evidence: `oidcc-3rdparty-init-login-certification-test-plan-code-oIuR7NYEowEAg-15-Nov-2026.zip`,
  `3rdparty-init-login-op-plan-oIuR7NYEowEAg.json`, `3rdparty-init-login-op-local-run-1.log`,
  plus the evidence-index section.

Exit criteria: focused tests pass, both plan modules complete, package exported. ✓ (local round; public round + package pending)

### 4. FAPI 2.0 Security Profile Final (conditional)

Plan: `fapi2-security-profile-final-test-plan`

The code already has many FAPI-relevant primitives:

- PAR and PKCE S256
- `authorization_response_iss_parameter_supported=true`
- JARM and signed request objects
- `private_key_jwt` and mTLS/self-signed certificate authentication
- certificate-bound access tokens and `mtls_endpoint_aliases`
- DPoP

Gaps to assess before committing:

- DCR currently rejects `request_object_signing_alg`,
  `request_object_encryption_alg`, and `request_object_encryption_enc`.
- `dpop_bound_access_tokens` is advertised as `false`; the mTLS variant is the safer
  first target.
- FAPI 2.0 requires mandatory PAR behavior, mTLS endpoint aliases, and holder-of-key
  enforcement.
- The deployment needs a separate mTLS endpoint/alias if the suite's mTLS variant
  requires one.

Do a discovery + happy-path gap run before scheduling the full plan. Do not promise a
FAPI certification date until that run is clean.

### 5. FAPI 1.0 Advanced Final (conditional)

Plan: `fapi1-advanced-final-test-plan`

This is the legacy FAPI profile. It requires signed request objects (passed by value or
via PAR), PKCE, mTLS/private-key JWT authentication, and holder-of-key behavior. Only
pursue it if a customer or an open-banking ecosystem explicitly requires FAPI 1.0
Advanced.

### 6. FAPI-CIBA (conditional)

Plan: `fapi-ciba-id1-test-plan`

CIBA is implemented, but FAPI-CIBA adds signed request objects, binding messages,
holder-of-key enforcement, and FAPI-specific error semantics. Treat this as a gap
assessment, not a quick add-on. Only pursue it if Open Banking CIBA is a target.

### 7. FAPI 2.0 Message Signing (optional)

Plan: `fapi2-message-signing-final-test-plan`

JAR/JARM support exists, but message-signing certification adds strict request-object and
JARM profiles. Consider it only after the FAPI 2.0 Security Profile gap run, and only if
message-signing certification has a business use.

## Not Appropriate

- **Hybrid OP, Form Post Hybrid OP, Form Post Implicit OP**: the OP deliberately
  supports only `code`, `id_token`, and `id_token token` response types. The OIDCD-3
  (Dynamic OP) profile required implicit support, which is implemented
  (`response_types_supported` includes `id_token`/`id_token token`, `grant_types_supported`
  includes `implicit`, DCR accepts and auto-pairs implicit response types with the
  `implicit` grant). The standalone **Implicit OP / Form Post Implicit OP** profiles are
  redundant with what Dynamic OP already certifies, and hybrid flows
  (`code id_token*`, `code token`, `id_token token`-with-code) remain unsupported by
  design. Adding them would require broad implementation and security work for
  deprecated flows.
- **OIDC4IDA / eKYC**: the suite's eKYC plan is not currently part of the certification
  program, and there is no `verified_claims` support.
- **OpenID4VCI Issuer**: no credential issuance endpoints or credential configuration.
- **OpenID4VP Verifier/Wallet**: no presentation or wallet role.
- **OpenID Federation**: no federation entity or trust-chain support.
- **Shared Signals Framework and AuthZEN PDP**: different roles/features.
- **OIDC RP/client plans**: these certify a relying party, not the OP. They would only
  apply if a separate client component, such as the CLI or an example app, were being
  certified.

## Recommended Sequence

1. Run **Dynamic OP** against the chosen commit.
2. Run **Session Management OP** with manual browser evidence.
3. If breadth is wanted, implement `initiate_login_uri` and run
   **3rd Party-Initiated Login OP**.
4. Only with a financial/B2B driver, run the **FAPI 2.0 Security Profile Final** gap
   assessment (mTLS variant first).
5. Consider **FAPI 1.0 Advanced** or **FAPI-CIBA** only when a specific ecosystem
   requires them.
6. Skip Implicit/Hybrid and the credential/federation paths unless the product roadmap
   changes.

## Run Prerequisites and Artifact Rules

- Verify `/version` matches the intended commit before interpreting any failure as a
  protocol defect.
- Use one issuer, TLS certificate, and signing-key set for the whole run.
- Keep DCR off in production outside the certification window. Use an initial access
  token where possible; if anonymous registration is required temporarily, restore
  `Dcr__AllowAnonymousInProduction=false` immediately after the run.
- Rotate the hosted-suite token after the round; keep
  `tools/certification/conformance-token.txt` local and gitignored.
- Export one certification package per profile, archive it under
  `tools/certification/.generated/exports/`, and update the evidence index.
- Do not submit packages from different commits as one certification.

## Tracking

- [x] Dynamic OP (local round passed 2026-11-15, plan `9bZZT9xLeF6gR`; certification
      package pending the public-deployment round)
- [x] Session Management OP (local round passed 2026-11-15, plan `AXbS62CPeUnu6`;
      both modules PASSED, 0 failures/warnings; server-side session_state comparison
      iframe replaces the fetch/crypto.subtle script; certification package pending
      the public-deployment round)
- [x] 3rd Party-Initiated Login OP (local round passed 2026-11-15, plan `oIuR7NYEowEAg`;
      both modules PASSED, 0 failures/warnings; `initiate_login_uri` was already
      fully implemented, no code changes needed; certification package pending the
      public-deployment round)
- [ ] FAPI 2.0 Security Profile Final gap assessment
- [ ] FAPI 1.0 Advanced Final (only if required)
- [ ] FAPI-CIBA (only if required)
- [ ] FAPI 2.0 Message Signing (only if required)
