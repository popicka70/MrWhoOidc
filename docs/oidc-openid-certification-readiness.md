# OpenID Foundation Certification Readiness for MrWhoOidc.WebAuth

## Status

MrWhoOidc is **not** OpenID certified. No certification package has been submitted to the OpenID Foundation.

Hosted conformance runs in April–June 2026 (summarised below) were clean or near-clean for several profiles, but they ran against builds that predate the Phase 0 security fixes recorded in [oidc-idp-assessment-2026-10-04.md](oidc-idp-assessment-2026-10-04.md). Those fixes change protocol behaviour the suite exercises, so **every prior result must be re-run on the current build** before it is used as evidence. In particular:

- **Client authentication:** only public clients may authenticate without a secret. The token endpoint enforces the registered `token_endpoint_auth_method` (or the `AllowClientSecretBasic/Post/PrivateKeyJwt` toggles), rejects multiple methods and Basic/form `client_id` mismatches, and returns `401` + `WWW-Authenticate` for `invalid_client` when Basic was used.
- **Grant types:** registered `grant_types` are enforced (`refresh_token` is implied by `authorization_code`; clients with no registered grant types fall back to the per-grant toggles). DCR defaults to `grant_types=["authorization_code"]` and `token_endpoint_auth_method=client_secret_basic`.
- **PAR:** a `request_uri` is single-use and bound to the client that pushed it, and `RequirePar` (global, configured list, `Client.RequirePar`) applies to every non-PAR request.
- **Logout:** front- and back-channel notifications go only to RPs that received codes or hold live tokens for the verified subject, each with its own (pairwise-aware) `sub`. The subject comes from the OP session or a signature-verified `id_token_hint`.
- **Resource indicators:** `resource` is limited to `Auth:ApiAudiences` plus the client's `M2MAllowedAudiencesJson` (`invalid_target` otherwise), and a token-request `resource` must match the one bound to the code.
- **Encryption:** if a client registers ID-token or JARM encryption and the key cannot be resolved, the request fails instead of falling back to plaintext.

## How OpenID Certification Works

Certification is **self-certification** backed by the official conformance suite:

1. Choose the profiles. Run the suite for each one.
2. Get every module to `PASSED`, `REVIEW`, `WARNING` or `SKIPPED`. `FAILED` or `INTERRUPTED` blocks submission.
3. Export one ZIP per profile with **Publish for certification**.
4. Pay the fee for a payment code, submit the ZIPs through the portal and sign the Declaration of Conformance.

The suite itself is free to use. Certification applies to a specific deployment and version. Some modules need screenshot evidence, so the process is not fully headless.

Suite hosts: production `https://www.certification.openid.net/`, staging `https://staging.certification.openid.net/` (needs staging-specific redirect URIs), source `https://gitlab.com/openid/conformance-suite/`. Automation uses `scripts/run-test-plan.py` with `CONFORMANCE_SERVER`, `CONFORMANCE_SERVER_LOCAL`, `CONFORMANCE_SERVER_MTLS` and config placeholders (`{BASEURL}`, `{LOCALBASEURL}`, `{HOSTNAME}`, `{BASEURLMTLS}`).

## Target Profiles

| Profile | Plan | Notes |
| --- | --- | --- |
| Config OP | `oidcc-config-certification-test-plan` | Allow the known discovery extension fields in `server.allow_unexpected_metadata_fields`. |
| Basic OP | `oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=dynamic_client]` | Dynamic client registration is preferred because it does not depend on seeded aliases. |
| Form Post OP | `oidcc-formpost-basic-certification-test-plan[server_metadata=discovery][client_registration=dynamic_client]` | |
| RP-Initiated Logout OP | `oidcc-rp-initiated-logout-certification-test-plan` | Required for any logout submission. |
| Session Management OP | `oidcc-session-management-certification-test-plan` | The suite's htmlunit browser cannot run the `check_session_iframe` JS (`crypto.subtle` + `postMessage`). Complete the module interactively in a real browser. |
| Front-Channel Logout OP | `oidcc-frontchannel-rp-initiated-logout-certification-test-plan` | |
| Back-Channel Logout OP | `oidcc-backchannel-rp-initiated-logout-certification-test-plan` | |
| Dynamic OP | — | Possible: DCR (RFC 7591/7592) is implemented, but it needs its own verification run. |

**Implicit OP** and **Hybrid OP** are not targets. Discovery advertises `response_types_supported=["code"]`, and DCR accepts only `code`.

Logout rules: a submission must include RP-Initiated Logout OP plus at least one of the other three logout profiles. Each logout profile is submitted separately, for every supported `response_type`. Without DCR, logout clients need `post_logout_redirect_uris`, `frontchannel_logout_uri` and `backchannel_logout_uri` under `https://www.certification.openid.net/test/a/<ALIAS>/…`, with both `*_session_required = true`.

Static-client alternative: register two `client_secret_basic` clients and one `client_secret_post` client with callback `https://www.certification.openid.net/test/a/<ALIAS>/callback`. The seed manifest template provides `oidf-basic-primary`, `oidf-basic-secondary` and `oidf-basic-client-secret-post`.

## Expected Non-Pass Outcomes (Basic OP, last run)

- **REVIEW** (screenshot evidence): `oidcc-display-page`, `oidcc-display-popup`, `oidcc-prompt-login`, `oidcc-max-age-1`, `oidcc-ensure-registered-redirect-uri`.
- **WARNING:** `oidcc-ensure-post-request-succeeds`. The suite did not see the redirect within its 30 s window.
- **SKIPPED** (intentionally unsupported):
  - unsigned ID tokens: `oidcc-idtoken-unsigned`
  - `address`/`phone` scopes, which are not advertised by default: `oidcc-scope-address`, `-phone`, `-all`
  - unsigned request objects and unsigned `request_uri`: `oidcc-request-uri-unsigned-…`, `oidcc-unsigned-request-object-…`, `oidcc-ensure-request-object-with-redirect-uri`

`scopes_supported` is built from the exposed scopes in the database, so the scope skips depend on what is seeded.

A past `prompt=none` failure came from a cached `/Auth/Redirect` page. That page now sends `Cache-Control: no-store, no-cache, max-age=0` and is covered by `MrWhoOidc.UnitTests/RedirectPageTests.cs`.

## Prior Evidence (pre-Phase 0, must be re-run)

All runs targeted `https://mrwho.onrender.com/t/default`, last recorded 2026-06-22 against commit `022e58cd`:

- Config OP: clean pass.
- Basic OP (dynamic): plan `hfLvYIxzl7fFq` had no FAILED, INTERRUPTED or NOT RUN modules. Only the REVIEW, WARNING and SKIPPED outcomes listed above remained.
- Form Post OP (dynamic): full matrix with no failures. REVIEW screenshots were uploaded.
- RP-Initiated Logout OP: plan `x5WpxffzGWzG4`, 0 failures.
- Back-Channel Logout OP: plan `qTbSGMpNjZsSE`, 0 failures.
- Front-Channel Logout OP: plan `tj3c0qM3zUwmW`, 0 failures.
- Session Management OP: the interactive module was not completed.
- Verifier: the public verifier's only warning was the missing `oidf-basic-client-secret-post` seed client.

## Pre-Run Checks on the Current Build

- Discovery must match runtime behaviour. Two gaps to check:
  - DCR accepts `token_endpoint_auth_method=none`, but discovery does not list `none`.
  - Discovery lists `self_signed_tls_client_auth`, but DCR does not accept it.
- Keep one tenant issuer, its TLS certificate and its signing keys stable for the whole run. Key rotation now runs for every active tenant.
- DCR must be enabled (`Auth:EnableDynamicClientRegistration`, default `false`). The suite registers without a token, so the deployment needs `Auth:RequireInitialAccessToken=false` (the default).
- If static clients are used, the deployment's `Seeding__ManifestJson`/`Seeding__ManifestBase64` must be updated and reapplied (`POST /bootstrap/apply-seed-manifest`). Redeploying code does not update it.
- Confirm `/version` matches the commit you intend to certify before you read any failure as a protocol defect.

## Repository Harness

`tools/certification/` holds the runbook (`README.md`). It also contains:

- `start-self-certification.ps1`: local stack plus seed manifest
- `verify-self-certification.ps1`: discovery, JWKS, logout metadata, DCR and fallback clients
- `prepare-conformance-suite.ps1`: runner inputs under `.generated/`
- `invoke-official-run-test-plan.ps1`: wraps `run-test-plan.py`
- `docker-compose.certification.dev.yml`
- `templates/certification-seed-manifest.template.json`
- `capture-review-screenshots.py`

Store the exported ZIPs and logs as build artifacts for auditability.

## Sources

- https://openid.net/certification/
- https://openid.net/how-to-certify-your-implementation/
- https://openid.net/how-to-submit-your-certification-request/
- https://openid.net/certification/about-conformance-suite/
- https://openid.net/certification/connect_op_testing/
- https://openid.net/certification/connect_op_logout_testing/
- https://gitlab.com/openid/conformance-suite/
