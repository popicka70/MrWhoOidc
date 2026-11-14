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
  background rotation service exists, but confirm a rotation can be triggered during the
  test.
- Run against the exact commit that will become the certification target.

Exit criteria: all modules PASSED/REVIEW/SKIPPED as expected, no unexpected FAILURE or
WARNING, package exported and indexed.

### 2. Session Management OP

Plan: `oidcc-session-management-certification-test-plan`

Why it fits:

- `check_session_iframe` and `end_session_endpoint` are implemented and advertised.
- The existing logout work covers the RP-initiated part of the flow.

Prerequisites and caveats:

- The suite's htmlunit browser cannot execute the `check_session_iframe` JavaScript
  (`crypto.subtle` + `postMessage`). Complete the interactive module in a real browser
  and capture the required evidence.
- Keep the OP session store configuration stable for the whole run.

Exit criteria: both modules completed with expected results and manual evidence attached.

### 3. 3rd Party-Initiated Login OP

Plan: `oidcc-3rdparty-init-login-certification-test-plan`

Current gap:

- DCR and RFC 7592 explicitly reject `initiate_login_uri`.
- The client model and registration/configuration responses do not carry the field.

Work needed before running:

- Add storage for `initiate_login_uri`.
- Accept it during dynamic registration and client configuration updates.
- Require an absolute HTTPS URI; reject non-HTTPS values with
  `invalid_client_metadata`.
- Echo it in the registration response and the client configuration response.
- Add tests for HTTPS acceptance, non-HTTPS rejection, and response round-tripping.

The plan only checks registration/configuration behavior and non-HTTPS rejection; it
does not drive a complete initiate-login flow. This is a small feature, not a config
toggle.

Exit criteria: focused tests pass, both plan modules complete, package exported.

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

- **Implicit OP, Hybrid OP, Form Post Implicit OP, Form Post Hybrid OP**:
  discovery advertises only `response_types_supported=["code"]`, and DCR only accepts
  code response types. Adding these deprecated flows would require broad implementation
  and security work.
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

- [ ] Dynamic OP
- [ ] Session Management OP
- [ ] 3rd Party-Initiated Login OP (`initiate_login_uri` work)
- [ ] FAPI 2.0 Security Profile Final gap assessment
- [ ] FAPI 1.0 Advanced Final (only if required)
- [ ] FAPI-CIBA (only if required)
- [ ] FAPI 2.0 Message Signing (only if required)
