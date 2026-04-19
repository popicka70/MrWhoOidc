# OIDC Self-Certification Harness

This folder contains the repo-side bootstrap for OpenID Foundation self-certification work against `MrWhoOidc.WebAuth`.

What it does:

- renders a seed manifest with OpenID Foundation callback and logout URIs
- starts the local WebAuth certification issuer through Docker Compose
- enables Dynamic Client Registration prerequisites for the certification stack, including a deterministic initial access token
- verifies the issuer contract needed for `Config OP`, `Basic OP`, and the locally testable portions of `Dynamic OP`
- renders repo-managed hosted-suite and official runner inputs

This harness does not submit results to the OpenID Foundation and does not attempt to drive the hosted suite UI. It prepares a stable issuer that can be targeted from the official conformance suite.

## Files

- `start-self-certification.ps1` - renders the certification manifest, starts the stack, and runs verification
- `verify-self-certification.ps1` - checks discovery fidelity, JWKS, negative authorize behavior, PAR when available, DCR CRUD, logout metadata, and seeded clients
- `prepare-conformance-suite.ps1` - renders hosted-suite inputs, suite API environment variables, starter official-runner config JSON files with the issuer URL embedded directly, and empty expected-failure / expected-skip files
- `invoke-official-run-test-plan.ps1` - wraps the official `run-test-plan.py` script with the correct suite API environment and rewrites any placeholder-based JSON config arguments for this issuer
- `docker-compose.certification.dev.yml` - Compose overlay that mounts the generated manifest and enables certification-specific settings
- `templates/certification-seed-manifest.template.json` - template for the seeded certification clients

## Default Clients

The seed manifest creates these fallback clients under tenant `default`:

- `oidf-basic-primary`
- `oidf-basic-secondary`

Client secrets:

- `oidf-basic-primary-dev-secret`
- `oidf-basic-secondary-dev-secret`

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

- set `Bootstrap__Token` on the deployment
- set `Auth__EnableDynamicClientRegistration=true`
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
- fallback OIDF clients such as `oidf-basic-primary` and `oidf-basic-secondary`
- the dedicated certification browser user and its client assignments

Remove `Bootstrap__Token` again after the manifest has been applied.

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

## Prepare Conformance-Suite Inputs

```powershell
pwsh ./tools/certification/prepare-conformance-suite.ps1
```

This renders the following files under `tools/certification/.generated/`:

- `conformance-suite-inputs.json`
- `conformance-suite-notes.md`
- `conformance-suite-env.ps1`
- `expected-failures.json`
- `expected-skips.json`
- `official-runner-static-op-config.json`
- `official-runner-dynamic-op-config.json`

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

This wrapper does not invent suite config JSON for you. It keeps the suite API environment contract, export directory, and expected-failure / expected-skip files consistent with the local certification issuer.

`prepare-conformance-suite.ps1` now also emits starter official-runner config JSON files for static-client and dynamic-client OP runs, including browser automation for provider selection, login, consent, and callback completion. These are intended as practical starting points for `Config OP`, `Basic OP`, and `Form Post OP` plans. The generated JSON files carry the issuer-under-test discovery URL directly; `CONFORMANCE_SERVER*` stays reserved for the conformance-suite API host.

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
