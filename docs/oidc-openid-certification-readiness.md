# OpenID Foundation Certification Readiness for MrWhoOidc.WebAuth

Last reviewed: 2026-06-21

## Purpose

This document summarizes what OpenID Connect certification would require for `MrWhoOidc.WebAuth`, based on OpenID Foundation materials and the current repository state.

OpenID Foundation terminology matters here:

- The Foundation describes the process as **self-certification** backed by the official **conformance suite**.
- There is no one-click "auto-certification" path. What can be automated is the repeated execution of conformance plans, environment setup, evidence collection, and regression gating before the formal submission.

## Executive Summary

For `MrWhoOidc.WebAuth`, the most realistic initial OpenID Connect certification targets appear to be:

- `Config OP`
- `Basic OP`
- `Form Post OP`
- logout profiles, submitted separately

`Dynamic OP` also looks plausible, but the repository's own compliance assessment identifies several dynamic-registration fidelity issues that should be fixed before relying on it for certification.

`Implicit OP` and `Hybrid OP` are not current targets because the server advertises and enforces `response_type=code` only.

The current repo-side runbook for recurring certification work is `tools/certification/README.md`.

## Last Known Hosted Certification Status

Treat this section as historical evidence from the last recorded hosted run. It is not a claim that the current build is still passing after subsequent changes.

Latest repo-verified state as of 2026-06-21:

- Public deployment `https://mrwho.onrender.com/t/default` at commit `e356915a8da84837f7aa449a678ef8e0ac5eac17` now passes the repo verifier with `61 passed, 0 failed, 7 warnings`.
- Those warnings are operational, not protocol failures:
  - alias `mrwhooidc-rum2c-20260612-configop` is no longer seeded for `oidf-basic-primary` and `oidf-basic-secondary`
  - fallback client `oidf-basic-client-secret-post` is still missing from the public deployment
  - static-client prompt/claims/authorization_details and static PAR checks therefore cannot be exercised on that deployment until the certification manifest is reapplied for the chosen alias
- Public Dynamic OP behavior is materially better than the earlier hosted notes suggest:
  - dynamic client registration create/get/update/get/delete succeeds end to end
  - dynamic authorize `prompt=none` returns the expected `login_required` redirect contract
  - dynamic PAR succeeds and returns `request_uri`
- Local certification stack validation with the seeded alias `mrwhooidc-local` completed with `81 passed, 0 failed, 0 warnings` through `tools/certification/start-self-certification.ps1`.

Latest hosted proof-of-concept evidence:

- As of 2026-06-12, public hosted plan `K9tn8mdLDppph` passed `Config OP` module `oidcc-discovery-endpoint-verification` with log `j41eGYNi8QJFolU` against `https://mrwho.onrender.com/t/default` using alias `mrwhooidc-rum2c-20260612-configop`.
- Result summary for that run: `SUCCESS 34`, `FAILURE 0`, `WARNING 0`, `REVIEW 0`.
- This confirms that the current public deployment is discoverable and that the repo-generated hosted-suite inputs are sufficient to create and pass a public `Config OP` proof run.
- As of 2026-06-12, `POST /bootstrap/apply-seed-manifest` also succeeds on the public deployment once `Seeding__ManifestBase64` is configured on Render and the temporary `Bootstrap__Token` is present.
- As of 2026-06-12, public unauthenticated dynamic client registration succeeds end-to-end against `https://mrwho.onrender.com/t/default/register`: `POST` returns `201 Created`, `GET` returns `200 OK`, and `DELETE` returns `204 No Content`.
- As of 2026-06-12, hosted `Basic OP` dynamic-client plan `sFRERuOH2zkUN` still fails on the first `oidcc-server` happy-path authorization because the public deployment returns `error=invalid_request` with `error_description=PKCE S256 is required for this client` for the dynamically registered confidential client.
- The public deployment still shows only `oidf-basic-primary` and `oidf-basic-secondary` in the live client list; `oidf-basic-client-secret-post` was not observed after the successful manifest apply and should be treated as a remaining static-client discrepancy.
- Operational conclusion for the next rerun at that time: the initial-access-token blocker had been removed, but `Basic OP` dynamic-client was still blocked by then-current public authorization behavior for dynamically registered web clients, and static-client `Basic OP` was still blocked by the missing third fallback client.

As of 2026-04-19, the repo-side harness and hosted-suite wrapper are operational against the public issuer `https://mrwho.onrender.com/t/default`.

Current observed status:

- `Config OP` has a clean hosted pass via the official runner.
- Public static-client `Basic OP` remains operationally blocked until the deployment is reseeded for the target alias and the third fallback `client_secret_post` client is restored.
- Public Dynamic OP smoke is currently healthy in the repo verifier: RFC 7591 registration, RFC 7592 management round-trips, dynamic authorize `prompt=none`, and PAR all succeed.
- Local seeded certification verification is currently clean across discovery, Basic OP style static clients, logout readiness, PAR, and Dynamic OP smoke.
- `Form Post OP` and hosted interactive static-client reruns should now be treated primarily as deployment-seeding and hosted-suite execution work, not as a known local product blocker.

Historical hosted evidence captured during earlier sessions:

- `Config OP` hosted plan `kPISpUXPFNj09` passed module `oidcc-discovery-endpoint-verification` (`aGuiYE4kvLmpkJQ`).
- `Basic OP` hosted smoke plan `PoXzUUdchUmUi` reached the login page and submitted credentials, but module `gmaLBZcVOvzmFCy` failed with `Invalid username or password` for `oidf-cert-user`.
- Explicit hosted `Basic OP` dynamic-client plan `cS4Aa781pSrOi` created and ran, proving the repo-side wrapper and official runner integration are working.
- The hosted suite's earlier dynamic registration blocker was that `POST /register` without an `Authorization` header returned `401 invalid_token` with `Initial access token required`; this was removed by setting `AuthOptions.RequireInitialAccessToken=false` on the public certification deployment.
- Public hosted `Basic OP` dynamic-client plan `sFRERuOH2zkUN` then progressed past registration, but module `oidcc-server` log `5cFSpE36qvl3KT3` failed with `CheckIfAuthorizationEndpointError` because the authorization endpoint returned `error=invalid_request` and `error_description=PKCE S256 is required for this client`.

Important operational conclusion:

- `POST /bootstrap/apply-seed-manifest` only reapplies the manifest currently configured on the deployment.
- Deploying updated code or regenerating `tools/certification/.generated/certification-seed-manifest.json` does not by itself update the live `Seeding__ManifestJson` or `Seeding__ManifestBase64` value.
- Until the deployment configuration is updated to the newer manifest and reapplied, hosted interactive static-client OP profiles remain blocked even when the underlying runtime behavior is otherwise healthy.

## Inputs Required To Rerun

For a practical rerun on the current build, the operator needs to choose or provide:

- the certification target issuer and tenant slug
- the suite host: production hosted, staging hosted, or a local conformance-suite environment
- the exact profiles in scope for this pass
- an explicit alias for the run when static seeded clients are involved
- access to update and reapply the certification seed manifest on the target deployment if the run is not local-only
- confirmation that the target deployment exposes stable signing keys and truthful discovery metadata for the whole run
- a local checkout of the official `conformance-suite` repository plus Python if the upstream runner wrapper will be used
- hosted-suite token or operator access if the upstream runner requires authenticated API calls
- a payment code only if the outcome is intended for formal OpenID Foundation submission rather than an internal regression rerun

## Frequent-Rerun Workflow

The repeatable path is now:

1. Render a fresh certification manifest for the chosen alias.
2. Apply that manifest to the target deployment when the issuer is public or otherwise long-lived.
3. Verify `/version`, `/health`, discovery metadata, and seeded fallback clients before involving the official suite.
4. Generate fresh runner inputs under `tools/certification/.generated/` for the same alias and issuer.
5. Run the selected hosted or upstream runner plans and archive the generated config, notes, and export artifacts.

This workflow is operationally documented in `tools/certification/README.md` and should be treated as the source of truth for reruns.

## What the OpenID Foundation Requires

Based on the official certification pages, the certification flow is:

1. Choose one or more certification profiles.
2. Run the OpenID Foundation conformance suite for each target profile.
3. Get every test in the target profile to one of these states: `PASSED`, `REVIEW`, `WARNING`, or `SKIPPED`.
4. Ensure there are no `FAILED` or `INTERRUPTED` results in the profile being submitted.
5. Use the suite's `Publish for certification` action to export a ZIP file for each profile.
6. Pay the certification fee and obtain a payment code.
7. Submit the ZIP file(s) through the OpenID Foundation submission portal.
8. Complete the signed Declaration of Conformance.

Additional points from the official process:

- Using the conformance suite itself is free. The fee applies to the actual certification submission.
- The certification request is per deployment / version, not an abstract product family.
- If you certify multiple profiles, you submit one exported result ZIP per profile.
- Some tests may require manual attention or uploaded evidence. Certification should not be treated as fully headless from start to finish.

## Official Test Infrastructure and Automation Options

The OpenID Foundation provides several ways to run the suite:

- Hosted suite: `https://www.certification.openid.net/`
- Staging suite: `https://staging.certification.openid.net/`
- Open-source suite source: `https://gitlab.com/openid/conformance-suite/`
- Local Docker-capable suite: referenced by the Foundation on the conformance-suite information page
- Official Python runner for automation: `scripts/run-test-plan.py` in the conformance-suite repository

Important practical notes from the official materials:

- The staging environment may expose fixes before they reach the production hosted suite.
- If you use staging, you need staging-specific redirect URIs in your test clients.
- The OpenID Foundation explicitly recommends using the provided automation tooling in development pipelines.

The current `run-test-plan.py` script in the official suite shows that CI-style automation is built around configuration files plus environment variables such as:

- `CONFORMANCE_SERVER`
- `CONFORMANCE_SERVER_LOCAL`
- `CONFORMANCE_SERVER_MTLS`

It also replaces config placeholders such as `{BASEURL}`, `{LOCALBASEURL}`, `{HOSTNAME}`, and `{BASEURLMTLS}`. In practice, that means a reproducible MrWhoOidc certification harness should keep suite config templates under source control and inject only environment-specific values at runtime.

## Certification Profiles Relevant to MrWhoOidc.WebAuth

### Strong Initial Targets

| Profile | Why it fits | Current repository evidence |
| --- | --- | --- |
| `Config OP` | The server publishes discovery metadata and JWKS. | `docs/oidc-conformance-checklist.md`, `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` |
| `Basic OP` | The server supports the authorization code flow and token issuance. | `docs/oidc-conformance-checklist.md`, `MrWhoOidc.Auth/Services/Authorization/AuthorizeRequestValidator.cs` |
| `Form Post OP` | The server advertises and tests `response_mode=form_post`. | `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`, `MrWhoOidc.UnitTests/AuthorizeHandlerTests.cs` |

### Likely Targets With More Preparation

| Profile | Why it may fit | Current caution |
| --- | --- | --- |
| `Dynamic OP` | Dynamic client registration and management endpoints exist. | Existing repo assessment flags DCR round-trip and metadata-enforcement gaps. |
| `RP-Initiated Logout OP` | Logout is implemented and this profile is required for any logout certification submission. | Requires dedicated logout test execution and its own submission ZIP. |
| `Session Management OP` | Session management support exists. | Needs explicit suite coverage and profile-specific evidence. |
| `Front-Channel Logout OP` | Front-channel logout exists. | Requires exact suite client settings and separate profile submission. |
| `Back-Channel Logout OP` | Back-channel logout exists. | Requires exact suite client settings and separate profile submission. |

### Not Current Targets

| Profile | Why it is not currently realistic |
| --- | --- |
| `Implicit OP` | The server enforces `response_type=code` only. |
| `Hybrid OP` | The server enforces `response_type=code` only. |

Notes:

- The OpenID Foundation OP instructions say most providers test `Basic OP`, and then `Implicit OP` and `Hybrid OP` only if those response types are actually supported.
- The same instructions call out `Config OP` and `Dynamic OP` as separate profile choices.
- Logout certification is separate from the non-logout OP profiles.

## Special Rules for Logout Certification

The Foundation's logout testing page is more specific than the generic OP page:

- Logout certification is split into four profiles:
  - `RP-Initiated Logout OP`
  - `Session Management OP`
  - `Front-Channel Logout OP`
  - `Back-Channel Logout OP`
- A logout certification submission must include `RP-Initiated Logout OP` plus at least one of the other three logout profiles.
- Each supported logout profile is submitted separately.
- The logout conformance profiles require submission for all `response_type` values supported by the implementation.

For suite setup without dynamic registration, the Foundation states that logout test clients need values like:

- `post_logout_redirect_uris = https://www.certification.openid.net/test/a/<ALIAS>/post_logout_redirect`
- `frontchannel_logout_uri = https://www.certification.openid.net/test/a/<ALIAS>/frontchannel_logout`
- `frontchannel_logout_session_required = true`
- `backchannel_logout_uri = https://www.certification.openid.net/test/a/<ALIAS>/backchannel_logout`
- `backchannel_logout_session_required = true`

## What MrWhoOidc.WebAuth Would Need Operationally

### 1. A Single Stable Certification Deployment

The conformance suite tests one issuer configuration at a time. For this repository, that means:

- Pick one tenant / issuer to certify.
- Keep its issuer URL stable for the full test run.
- Use a trusted HTTPS certificate.
- Keep discovery, JWKS, token, userinfo, logout, and registration endpoints reachable from the suite.

Because MrWhoOidc is multi-tenant and path-based, the cleanest approach is to certify one dedicated tenant-specific issuer rather than treating the whole multi-tenant product surface as one target.

### 2. Controlled Test Data

You will need predictable certification fixtures:

- One or more known test users.
- Deterministic client registrations or a trustworthy dynamic registration path.
- Stable redirect URIs.
- Stable signing keys during a run.

For the current hosted setup, that specifically includes a deployment-configured certification user seeded through the manifest. The public deployment has already shown that code changes alone are not enough; the manifest value on the host must be updated and then reapplied.

Unplanned key rotation or configuration churn during a test plan can create false failures.

### 3. Client Registration Strategy

The official OP instructions say:

- If the OP supports Dynamic Client Registration, use it for Basic / Implicit / Hybrid profile testing.
- Otherwise manually register three clients:
  - one `client_secret_basic` client
  - a second `client_secret_basic` client
  - one `client_secret_post` client

The suite also requires a unique `ALIAS`, and the callback URI contains that alias:

- `https://www.certification.openid.net/test/a/<ALIAS>/callback`

For MrWhoOidc, this means either:

- make `Dynamic OP` reliable enough that the suite can drive registration automatically, or
- pre-create the required clients and keep their metadata aligned with the suite's expected redirect and logout URIs.

### 4. Truthful Discovery Metadata

Certification depends heavily on discovery metadata matching real runtime behavior. For this project, that means the published metadata must stay perfectly aligned with:

- supported `response_types`
- supported `response_modes`
- registration support
- logout capabilities
- mTLS-related metadata
- JARM-related metadata if advertised

This matters because the conformance suite will use the discovery document as the contract.

### 5. Repeatable Automation Around the Suite

If the goal is sustainable "auto-certification" preparation, the repository now already contains most of the local harness pieces:

- checked-in certification scripts under `tools/certification/`
- a seed manifest template for fallback clients, logout URIs, and the dedicated certification user
- rendered runner input scaffolding for the official conformance-suite automation path

The main remaining work is operational rather than foundational:

- keep the public certification deployment configuration synchronized with the rendered manifest
- rerun the targeted plans on the current build and refresh the evidence set
- add pipeline-level artifact retention if you want this to be part of routine release gating

## Repository-Specific Readiness Assessment

### Current Strengths

Based on the repository's own documentation and code references:

- Discovery and JWKS are implemented.
- Authorization code flow is implemented.
- `response_mode=form_post` is implemented and tested.
- Dynamic client registration endpoints exist.
- Session management exists.
- RP-initiated, front-channel, and back-channel logout are implemented.

### Likely Certification Risks From Existing Repo Assessments

The current repo already documents several issues that are directly relevant to certification readiness.

#### Dynamic Client Registration risk

`docs/oidc-spec-compliance-assessment-2026-03-10.md` identifies several items that make `Dynamic OP` a risky early target:

- `jwks_uri` is accepted but not consistently honored across runtime validation paths.
- registration create / read / update behavior is not fully round-trippable.
- `require_auth_time` is persisted but not enforced during ID token issuance.
- `sector_identifier_uri` validation is stronger at runtime than at registration time.

If `Dynamic OP` is in scope, those items should be treated as likely blockers until re-tested against the suite.

#### Discovery contract drift

The same repo assessment notes a mismatch between implemented discovery behavior and at least one integration test around `tls_client_certificate_bound_access_tokens`.

Even when that does not block the chosen OIDC profile directly, it is exactly the kind of drift the `Config OP` profile exposes.

#### Integration coverage gaps

The repo's own conformance checklist still recommends stronger integration coverage for some advanced behaviors. While the OpenID Foundation suite is the real source of truth for certification, local integration gaps usually make certification work slower and more expensive.

## Recommended Certification Order for MrWhoOidc

1. Make `Config OP` pass first.
2. Make `Basic OP` pass second.
3. Add `Form Post OP` once the same environment is stable.
4. Decide whether `Dynamic OP` is worth near-term effort; if yes, fix the documented DCR fidelity issues before depending on the suite result.
5. Tackle logout certification separately, starting with `RP-Initiated Logout OP` plus the logout profile most aligned with product priorities.

That order keeps the initial scope aligned with what the server already appears to support, instead of forcing new product features purely for certification.

## Canonical Hosted Execution Order

For repeat public self-certification against `https://mrwho.onrender.com/t/default`, use this order and these exact plan names unless a later suite update requires adjustment:

1. `Config OP`
  - `oidcc-config-certification-test-plan`
  - current public status: clean hosted pass after adding known extension fields to `server.allow_unexpected_metadata_fields`

1. `Basic OP` with dynamic client registration
  - `oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=dynamic_client]`
  - current public status: full hosted run no longer shows protocol failures; remaining non-pass outcomes are `REVIEW` evidence checkpoints and `SKIPPED` cases

1. `Form Post OP` with dynamic client registration
  - `oidcc-formpost-basic-certification-test-plan[server_metadata=discovery][client_registration=dynamic_client]`
  - use dynamic-client first because public static-client reruns still depend on alias reseeding

1. `RP-Initiated Logout OP`
  - `oidcc-rp-initiated-logout-certification-test-plan`

1. `Session Management OP`
  - `oidcc-session-management-certification-test-plan`

1. `Front-Channel Logout OP`
  - `oidcc-frontchannel-rp-initiated-logout-certification-test-plan`

1. `Back-Channel Logout OP`
  - `oidcc-backchannel-rp-initiated-logout-certification-test-plan`

Execution notes:

- prefer the dynamic-client variants first on the public deployment because they are less sensitive to stale seeded callback URIs
- do not treat `REVIEW` as a product failure; it means the suite expects uploaded screenshot evidence for that step
- do not treat the current public static-client hosted path as canonical until the deployment is reseeded for the target alias and the third fallback client is restored

## Concrete Work Items Remaining

The current practical work package is now:

1. Choose the current certification target issuer, suite host, alias, and profile scope for the rerun.
2. Keep the public certification deployment's `Seeding__ManifestJson` or `Seeding__ManifestBase64` synchronized with the repo-generated certification manifest before rerunning hosted interactive profiles.
3. Reapply the manifest and confirm the deployed build version, health, discovery metadata, fallback clients, and certification user on the target issuer.
4. Rerun `Config OP`, `Basic OP`, and `Form Post OP` against the current build and archive the generated evidence.
5. Decide whether `Dynamic OP` is in scope for this cycle; if yes, revalidate the initial-access-token expectation and the documented DCR fidelity gaps first.
6. Align discovery metadata, tests, and docs so `Config OP` still reflects actual runtime behavior after recent product changes.
7. Store exported test-plan ZIPs and logs as build artifacts for auditability.
8. Document the manual submission steps for any run intended for formal certification: payment code, submission portal, and Declaration of Conformance signing.

## Suggested Non-Goals for the First Pass

To keep scope defensible, the first certification effort should probably avoid:

- adding implicit or hybrid response types solely for certification
- expanding into FAPI certifications before baseline OIDC profiles are passing
- certifying every logout profile in the first submission unless there is a clear product reason

## Recommended Next Step

The most efficient next operational step is to pick the target issuer and alias for the next rerun, synchronize the rendered manifest onto that deployment, and rerun `Config OP`, `Basic OP`, and `Form Post OP` with the existing harness under `tools/certification/`.

## Repository Harness

An initial repo-side certification harness now exists under `tools/certification/`.

- `tools/certification/start-self-certification.ps1` renders a certification seed manifest, starts the local WebAuth stack, enables DCR prerequisites for the certification environment, and runs a verifier.
- `tools/certification/verify-self-certification.ps1` checks discovery, JWKS, logout metadata, DCR routing, and fallback certification clients.
- `tools/certification/prepare-conformance-suite.ps1` renders hosted-suite inputs, environment-variable files, and expected-failure / expected-skip files for the official automation path.
- `tools/certification/invoke-official-run-test-plan.ps1` wraps the official `scripts/run-test-plan.py` entrypoint with the correct environment contract for the MrWhoOidc certification issuer.
- `tools/certification/docker-compose.certification.dev.yml` is the Docker Compose overlay for the certification issuer.
- `tools/certification/templates/certification-seed-manifest.template.json` contains the fallback clients and suite callback/logout URI pattern.

This starts the self-certification path at the environment level, even though actual OpenID Foundation test-plan execution and submission still happen through the official suite. The default green path now includes `Config OP`, `Basic OP`, and the DCR prerequisites needed for `Dynamic OP` discovery and routing. Full `Dynamic OP` certification still requires the metadata-fidelity fixes already called out elsewhere in this document.

The repo now also has the first thin layer of runner scaffolding: generated suite-input artifacts, a stable environment-variable contract (`CONFORMANCE_SERVER`, `CONFORMANCE_SERVER_LOCAL`, `CONFORMANCE_SERVER_MTLS`) reserved for the conformance-suite API host, starter OP config JSON files that carry the issuer-under-test URLs directly, and a wrapper that can call the upstream conformance-suite runner once official plan expressions and config JSON files are ready.

## Sources

Official external sources reviewed on 2026-04-09:

- `https://openid.net/certification/`
- `https://openid.net/how-to-certify-your-implementation/`
- `https://openid.net/how-to-submit-your-certification-request/`
- `https://openid.net/certification/about-conformance-suite/`
- `https://openid.net/certification/connect_op_testing/`
- `https://openid.net/certification/connect_op_logout_testing/`
- `https://gitlab.com/openid/conformance-suite/`
- `https://gitlab.com/openid/conformance-suite/-/raw/master/scripts/run-test-plan.py`

Internal repository references used to tailor this assessment:

- `docs/oidc-conformance-checklist.md`
- `docs/oidc-spec-compliance-assessment-2026-03-10.md`
- `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs`
- `MrWhoOidc.Auth/Services/Authorization/AuthorizeRequestValidator.cs`
- `MrWhoOidc.UnitTests/AuthorizeHandlerTests.cs`
