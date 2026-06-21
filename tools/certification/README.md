# OIDC Self-Certification Harness

This folder contains the repo-side bootstrap for OpenID Foundation self-certification work against `MrWhoOidc.WebAuth`.

What it does:

- renders a seed manifest with OpenID Foundation callback and logout URIs
- starts the local WebAuth certification issuer through Docker Compose
- enables Dynamic Client Registration prerequisites for the certification stack, including a deterministic initial access token
- verifies the issuer contract needed for `Config OP`, `Basic OP`, and the locally testable portions of `Dynamic OP`
- renders repo-managed hosted-suite and official runner inputs

This harness does not submit results to the OpenID Foundation and does not attempt to drive the hosted suite UI. It prepares a stable issuer that can be targeted from the official conformance suite.

## Operator Inputs For A Rerun

To rerun certification without rediscovering the setup each time, gather these inputs up front:

- target issuer base URL and tenant slug
- target suite host: `www.certification.openid.net` or `staging.certification.openid.net`
- explicit suite alias for the run; if you use seeded static clients, the same alias must be rendered into the certification manifest and applied to the target deployment before running the suite
- profiles in scope for this run: `Config OP`, `Basic OP`, `Form Post OP`, `Dynamic OP`, and/or logout profiles
- whether the run is local-only, hosted against a public deployment, or driven through a local checkout of the official conformance-suite repository
- deployment access for public runs: ability to update `Seeding__ManifestJson` or `Seeding__ManifestBase64`, set a temporary `Bootstrap__Token`, and confirm `Auth__EnableDynamicClientRegistration=true` plus `Auth__RequireInitialAccessToken=false`
- a local path to the official `conformance-suite` checkout plus Python if `invoke-official-run-test-plan.ps1` will be used
- hosted-suite token or operator login if the upstream runner needs authenticated API access
- payment code only when the goal is formal submission rather than regression verification

## Prerequisites

- Docker Compose to start the local certification issuer from `docker-compose.dev.yml` plus `docker-compose.certification.dev.yml`
- a publicly reachable HTTPS issuer if you want to use the hosted suite against anything other than a local-only smoke run; the hosted suite cannot reach `https://localhost:8443` unless you provide a tunnel or public deployment
- Python or the Windows `py` launcher if you want to call the upstream `scripts/run-test-plan.py` through `invoke-official-run-test-plan.ps1`
- a local checkout of the OpenID Foundation `conformance-suite` repository when using the upstream runner wrapper

## Files

- `start-self-certification.ps1` - renders the certification manifest, starts the stack, and runs verification
- `verify-self-certification.ps1` - checks discovery fidelity, JWKS, negative authorize behavior, PAR when available, DCR CRUD, logout metadata, and seeded clients
- `prepare-conformance-suite.ps1` - renders hosted-suite inputs, suite API environment variables, starter official-runner config JSON files with the issuer URL embedded directly, empty expected-failure / expected-skip files, and generated notes that call out additional relevant profiles such as `Dynamic OP` and the logout certification track; exact hosted-suite labels for those additional profiles can be supplied explicitly when known
- `invoke-official-run-test-plan.ps1` - wraps the official `run-test-plan.py` script with the correct suite API environment and rewrites any placeholder-based JSON config arguments for this issuer
- `capture-review-screenshots.py` - ad hoc Playwright helper for collecting screenshot evidence for `REVIEW` cases such as `prompt=login`, `max_age`, and invalid redirect URI behavior
- `docker-compose.certification.dev.yml` - Compose overlay that mounts the generated manifest and enables certification-specific settings
- `templates/certification-seed-manifest.template.json` - template for the seeded certification clients

## Default Clients

The seed manifest creates these fallback clients under tenant `default`:

- `oidf-basic-primary`
- `oidf-basic-secondary`
- `oidf-basic-client-secret-post`

Client secrets:

- `oidf-basic-primary-dev-secret`
- `oidf-basic-secondary-dev-secret`
- `oidf-basic-client-secret-post-dev-secret`

Dynamic registration initial access token:

- `oidf-dcr-initial-access-token`

These clients are intended for the official OP flow where the suite may require manually registered clients. The deployment also enables and advertises Dynamic Client Registration so you can test that path separately.

## Default Browser User

The certification manifest also seeds a dedicated interactive test user under tenant `default`:

- username: `oidf-cert-user`
- email: `oidf-cert-user@mrwho.local`
- password: `OidfCertUser123!`

The generated runner configs and `invoke-official-run-test-plan.ps1` now default to this account. You can override it with `-BrowserUsername` and `-BrowserPassword` when needed.

## Apply the Certification Manifest to an Existing Deployment

If a public deployment already has data and is missing the fallback OIDF clients or DCR settings, you can apply the rendered certification manifest without logging into the tenant admin UI.

Requirements:

- set `Seeding__Enabled=true`
- set `Seeding__AllowUpdates=true`
- set `Seeding__OverwriteClientSecrets=true`
- set `Bootstrap__Token` on the deployment
- set `Auth__EnableDynamicClientRegistration=true`
- set `Auth__EnableClientConfigurationEndpoint=true`
- provide the rendered certification manifest through one of the standard seeding inputs:
  - `Seeding__ManifestPath`
  - `Seeding__ManifestJson`
  - `Seeding__ManifestBase64`

Then call the operator endpoint:

```powershell
Invoke-RestMethod -Method Post `
  -Uri https://your-public-issuer.example/bootstrap/apply-seed-manifest `
  -Headers @{ 'X-Bootstrap-Token' = '<your bootstrap token>' }
```

This applies the configured manifest to the existing database, including:

- platform DCR enablement
- platform initial access tokens
- tenant DCR realm assignment
- fallback OIDF clients such as `oidf-basic-primary`, `oidf-basic-secondary`, and `oidf-basic-client-secret-post`
- the dedicated certification browser user and its client assignments

Remove `Bootstrap__Token` again after the manifest has been applied.

Important:

- `POST /bootstrap/apply-seed-manifest` reapplies the manifest currently configured on the deployment.
- Updating the repo template or generated JSON locally does not change the live deployment until you also update `Seeding__ManifestJson`, `Seeding__ManifestBase64`, or `Seeding__ManifestPath` there.

## Check the Deployed Build Version

To verify which build is running on a public deployment, query the public runtime version endpoint:

```powershell
Invoke-RestMethod -Method Get -Uri https://your-public-issuer.example/version
```

The response includes:

- `service`
- `environment`
- `version`
- `informationalVersion`
- `commit`

When the assembly metadata does not carry a commit suffix in the deployed environment, the runtime metadata now falls back to Render or CI environment variables such as `RENDER_GIT_COMMIT`, `RENDER_GIT_BRANCH`, and `RENDER_GIT_REPO_SLUG`.

For Render or any other Git-based deployment, you can verify that the live deployment matches the commit you expect:

```powershell
pwsh ./scripts/verify-deployed-version.ps1 `
  -BaseUrl https://your-public-issuer.example `
  -ExpectedCommit <git-sha> `
  -ExpectedBranch master
```

Or, when you run the script from the checked-out repository and want it to compare against local `HEAD` automatically:

```powershell
pwsh ./scripts/verify-deployed-version.ps1 `
  -BaseUrl https://your-public-issuer.example `
  -UseLocalGitHead `
  -ExpectedBranch master
```

The `/health` endpoint also includes the same runtime metadata under `runtime`, and both endpoints emit `X-MrWhoOidc-Version` headers so you can quickly confirm that a fresh deployment is live.

## Repeat-Run Checklist

1. Pick the suite host, target issuer, tenant slug, explicit alias, and profiles you want to rerun.
1. Render a fresh certification manifest for that alias.

```powershell
pwsh ./tools/certification/start-self-certification.ps1 `
  -Alias <alias> `
  -SuiteHost <suite-host> `
  -RenderOnly
```

1. If the issuer is a public deployment, update the deployment with the rendered `tools/certification/.generated/certification-seed-manifest.json`, set `Seeding__AllowUpdates=true` plus `Seeding__OverwriteClientSecrets=true`, then reapply it with `POST /bootstrap/apply-seed-manifest`. Confirm `Auth__EnableDynamicClientRegistration=true`, `Auth__EnableClientConfigurationEndpoint=true`, and keep `Auth__RequireInitialAccessToken=false` unless the suite is explicitly configured to send an initial access token.
1. Verify the deployed build and health endpoints before running the suite.

```powershell
Invoke-RestMethod -Method Get -Uri https://your-public-issuer.example/version
Invoke-RestMethod -Method Get -Uri https://your-public-issuer.example/health
```

1. For a local issuer, start and verify the certification stack directly.

```powershell
pwsh ./tools/certification/start-self-certification.ps1 `
  -Alias <alias> `
  -SuiteHost <suite-host>
```

1. Render the suite inputs and runner config files for the same alias and issuer.

```powershell
pwsh ./tools/certification/prepare-conformance-suite.ps1 `
  -Alias <alias> `
  -SuiteHost <suite-host> `
  -BaseUrl <issuer-base-url>
```

1. Run the hosted suite or the upstream `run-test-plan.py` runner with the generated config files, then archive the resulting `exports` directory plus the generated notes, inputs JSON, and expected-failure / expected-skip files.

Hosted-suite certification-package note:

- `POST /api/plan/<planId>/certificationpackage` requires the authenticated hosted-suite browser session.
- In the VS Code browser automation environment, the endpoint returns the ZIP successfully, but the browser-managed download may not be persisted automatically to disk.
- For the final submission handoff, trigger `Publish for certification` / `Create Certification Package` in a normal interactive browser session and verify the ZIP lands in the browser download directory.

Important:

- Do not rely on the wrapper's auto-generated alias for static-client reruns against a pre-seeded public deployment unless you also render and apply a manifest with that exact alias first.
- If you switch from production hosted suite to staging, regenerate the manifest because the redirect and logout URIs are host-specific.

## Start the Issuer

From the repository root:

```powershell
pwsh ./tools/certification/start-self-certification.ps1
```

Use a unique alias if needed:

```powershell
pwsh ./tools/certification/start-self-certification.ps1 -Alias mrwhooidc-yourname
```

Use the staging suite host instead of the production suite host:

```powershell
pwsh ./tools/certification/start-self-certification.ps1 -SuiteHost staging.certification.openid.net
```

## Verify the Issuer

```powershell
pwsh ./tools/certification/verify-self-certification.ps1
```

The verifier now reports profile-shaped readiness for:

- `RP-Initiated Logout OP`
- `Session Management OP`
- `Front-Channel Logout OP`
- `Back-Channel Logout OP`

It also performs a broader Dynamic Client Registration smoke round-trip by checking that `PUT /register/{client_id}` changes remain visible through a follow-up `GET`, including `default_max_age`, `require_auth_time`, and `contacts`.

## Prepare Conformance-Suite Inputs

```powershell
pwsh ./tools/certification/prepare-conformance-suite.ps1
```

When you know the exact hosted-suite labels for additional profiles, provide them explicitly so the generated notes and JSON artifacts can carry them forward:

```powershell
pwsh ./tools/certification/prepare-conformance-suite.ps1 `
  -DynamicOpPlanName <plan-name> `
  -RpInitiatedLogoutOpPlanName <plan-name> `
  -SessionManagementOpPlanName <plan-name> `
  -FrontChannelLogoutOpPlanName <plan-name> `
  -BackChannelLogoutOpPlanName <plan-name>
```

If you omit those parameters, the generated notes keep the profile in scope but mark the plan label as needing confirmation from the hosted suite.

This renders the following files under `tools/certification/.generated/`:

- `conformance-suite-inputs.json`
- `conformance-suite-notes.md`
- `conformance-suite-env.ps1`
- `expected-failures.json`
- `expected-skips.json`
- `official-runner-static-op-config.json`
- `official-runner-dynamic-op-config.json`

The generated `conformance-suite-notes.md` now distinguishes:

- immediate core profiles: `Config OP`, `Basic OP`, `Form Post OP`
- additional relevant profiles: `Dynamic OP`, `RP-Initiated Logout OP`, `Session Management OP`, `Front-Channel Logout OP`, and `Back-Channel Logout OP`

For logout work, the generated notes also restate the OpenID Foundation submission rule: include `RP-Initiated Logout OP` plus at least one of the other logout profiles.

## Invoke the Official Runner

If you have a local checkout of the official OpenID Foundation conformance-suite repository, you can wrap its `scripts/run-test-plan.py` entrypoint with the issuer-specific environment variables from this repo:

```powershell
$runnerArgs = @(
  '--list',
  '<test-plan expression as one string>',
  '<config-file path>'
)

& ./tools/certification/invoke-official-run-test-plan.ps1 `
  -ConformanceSuitePath C:\src\conformance-suite `
  -RunnerArguments $runnerArgs
```

If you pass one of the generated JSON runner config files and do not specify `-BaseUrl`, the wrapper infers the issuer base URL from that config's `server.discoveryUrl` instead of falling back to `https://localhost:8443`.

This wrapper does not invent suite config JSON for you. It keeps the suite API environment contract, export directory, and expected-failure / expected-skip files consistent with the local certification issuer.

`prepare-conformance-suite.ps1` now also emits starter official-runner config JSON files for static-client and dynamic-client OP runs, including browser automation for provider selection, login, consent, and callback completion. These are intended as practical starting points for `Config OP`, `Basic OP`, and `Form Post OP` plans. The generated JSON files carry the issuer-under-test discovery URL directly; `CONFORMANCE_SERVER*` stays reserved for the conformance-suite API host.

If you are targeting a public deployment with seeded static clients, pass `-Alias` explicitly so the runner config stays aligned with the alias already rendered into the deployment manifest.

## Expected Issuer

By default the harness targets:

- issuer: `https://localhost:8443/t/default`
- discovery: `https://localhost:8443/t/default/.well-known/openid-configuration`

## Hosted Suite Notes

For the official hosted suite at `https://www.certification.openid.net/`:

- use the same `ALIAS` value that you passed to `start-self-certification.ps1`
- target the issuer `https://localhost:8443/t/default` only if your machine is externally reachable from the suite, or if you are using a tunnel / public deployment
- otherwise use the same harness against a public certification environment and keep the alias, redirect URIs, and logout URIs aligned

The current starter scope is:

- `Config OP`
- `Basic OP`
- `Form Post OP`
- locally testable `Dynamic OP` coverage (`registration_endpoint`, seeded initial access token, and `/register` CRUD smoke)

The official hosted-suite `Dynamic OP` profile still depends on the OpenID Foundation runner and any additional profile-specific config it requires. The local harness now covers the repo-side DCR prerequisites and an end-to-end CRUD smoke path.

The current repo-managed runner scaffolding now covers:

- stable issuer bootstrap
- verifier checks for discovery fidelity, logout metadata, negative authorize flows, PAR when advertised, DCR CRUD, and fallback clients
- generated hosted-suite inputs and runner environment files
- a thin wrapper around the official `run-test-plan.py` automation entrypoint

## REVIEW Evidence

`capture-review-screenshots.py` is a narrow helper for evidence capture when the suite marks a case as `REVIEW` instead of `PASSED`. It currently has hardcoded issuer, user, token, redirect URI, and output directory values near the top of the file, so update those constants before running it against a different deployment.
