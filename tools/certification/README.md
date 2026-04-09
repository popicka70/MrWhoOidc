# OIDC Self-Certification Harness

This folder contains the repo-side bootstrap for OpenID Foundation self-certification work against `MrWhoOidc.WebAuth`.

What it does:

- renders a seed manifest with OpenID Foundation callback and logout URIs
- starts the local WebAuth certification issuer through Docker Compose
- enables Dynamic Client Registration prerequisites for the certification stack
- verifies the issuer contract needed for `Config OP` and `Basic OP`
- renders repo-managed hosted-suite and official runner inputs

This harness does not submit results to the OpenID Foundation and does not attempt to drive the hosted suite UI. It prepares a stable issuer that can be targeted from the official conformance suite.

## Files

- `start-self-certification.ps1` - renders the certification manifest, starts the stack, and runs verification
- `verify-self-certification.ps1` - checks discovery, JWKS, logout metadata, DCR routing, and seeded clients
- `prepare-conformance-suite.ps1` - renders hosted-suite inputs, runner environment variables, and empty expected-failure / expected-skip files
- `invoke-official-run-test-plan.ps1` - wraps the official `run-test-plan.py` script with the correct environment variables for this issuer
- `docker-compose.certification.dev.yml` - Compose overlay that mounts the generated manifest and enables certification-specific settings
- `templates/certification-seed-manifest.template.json` - template for the seeded certification clients

## Default Clients

The seed manifest creates these fallback clients under tenant `default`:

- `oidf-basic-primary`
- `oidf-basic-secondary`

Client secrets:

- `oidf-basic-primary-dev-secret`
- `oidf-basic-secondary-dev-secret`

These clients are intended for the official OP flow where the suite may require manually registered clients. The deployment also enables and advertises Dynamic Client Registration so you can test that path separately.

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

## Invoke the Official Runner

If you have a local checkout of the official OpenID Foundation conformance-suite repository, you can wrap its `scripts/run-test-plan.py` entrypoint with the issuer-specific environment variables from this repo:

```powershell
pwsh ./tools/certification/invoke-official-run-test-plan.ps1 `
  -ConformanceSuitePath C:\src\conformance-suite `
  -RunnerArguments @(
    '--list',
    '<test-plan expression as one string>',
    '<config-file path>'
  )
```

This wrapper does not invent suite config JSON for you. It keeps the environment contract, export directory, and expected-failure / expected-skip files consistent with the local certification issuer.

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
- Dynamic-registration prerequisites (`registration_endpoint` and `/register` routing)

`Dynamic OP` still remains a second-phase target overall, because metadata fidelity and round-trip behavior are not the same thing as merely advertising and routing `/register`.

The current repo-managed runner scaffolding now covers:

- stable issuer bootstrap
- verifier checks for discovery, logout metadata, DCR routing, and fallback clients
- generated hosted-suite inputs and runner environment files
- a thin wrapper around the official `run-test-plan.py` automation entrypoint
