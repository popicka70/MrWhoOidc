# OIDC Self-Certification Harness

This folder contains the repo-side bootstrap for OpenID Foundation self-certification work against `MrWhoOidc.WebAuth`.

What it does:

- renders a seed manifest with OpenID Foundation callback and logout URIs
- starts the local WebAuth certification issuer through Docker Compose
- enables the app-level Dynamic Client Registration flags and prepares the stack for second-phase DCR work
- verifies the issuer contract needed for `Config OP` and `Basic OP`

This harness does not submit results to the OpenID Foundation and does not attempt to drive the hosted suite UI. It prepares a stable issuer that can be targeted from the official conformance suite.

## Files

- `start-self-certification.ps1` - renders the certification manifest, starts the stack, and runs verification
- `verify-self-certification.ps1` - checks discovery, JWKS, logout metadata, DCR routing, and seeded clients
- `docker-compose.certification.dev.yml` - Compose overlay that mounts the generated manifest and enables certification-specific settings
- `templates/certification-seed-manifest.template.json` - template for the seeded certification clients

## Default Clients

The seed manifest creates these fallback clients under tenant `default`:

- `oidf-basic-primary`
- `oidf-basic-secondary`

Client secrets:

- `oidf-basic-primary-dev-secret`
- `oidf-basic-secondary-dev-secret`

These clients are intended for the official OP flow where the suite may require manually registered clients. The deployment also enables Dynamic Client Registration so you can test that path separately.

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

`Dynamic OP` remains a second-phase target. The harness enables the app-level DCR feature flags, but the platform-level DCR toggle and tenant realm selection are not yet automated here.