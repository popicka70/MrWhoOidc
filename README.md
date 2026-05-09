# MrWhoOidc

[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fpopicka70%2Fmrwhooidc-blue)](https://ghcr.io/popicka70/mrwhooidc)
[![Image Size](https://img.shields.io/badge/image%20size-%3C200MB-success)](https://ghcr.io/popicka70/mrwhooidc)
[![Multi-Arch](https://img.shields.io/badge/arch-amd64%20%7C%20arm64-informational)](https://ghcr.io/popicka70/mrwhooidc)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

A production-ready OpenID Connect (OIDC) and OAuth 2.0 provider built on .NET 10 with PostgreSQL, optional Redis caching, a tenant-aware admin UI, sample applications, and browser E2E coverage.

Source code in this repository is licensed under Apache 2.0. Use of the `MrWhoOidc` name, logos, and other brand assets is governed separately by [TRADEMARK_POLICY.md](TRADEMARK_POLICY.md).

## Local Development Quick Start

Estimated time: 3-5 minutes on a typical development machine. The commands below use `docker compose` (Compose V2). If your Docker installation still exposes the legacy `docker-compose` binary, replace the command form accordingly.

Start the full development stack, including seeded sample applications:

```bash
git clone https://github.com/popicka70/MrWhoOidc.git
cd MrWhoOidc

# Local development uses the dev compose file.
cp .env.example .env
# Edit .env if you want to override defaults.

docker compose -f docker-compose.dev.yml up -d --build

# Verify the auth server is up.
curl -k https://localhost:8443/t/default/.well-known/openid-configuration

# Run the broader smoke test.
bash ./scripts/verify-installation.sh
```

The discovery document should include fields such as `issuer`, `authorization_endpoint`, `token_endpoint`, and `jwks_uri`.

The development stack starts:
- MrWhoOidc WebAuth at `https://localhost:8443`
- PostgreSQL and Redis
- MailHog at `http://localhost:8025`
- OidcDemo at `https://localhost:5001`
- RazorClient at `https://localhost:5003`
- ReactOidcClient at `http://localhost:5173`
- TestApi at `https://localhost:7149`

Development mode auto-seeds the default tenant and admin account. Sign in with `admin@mrwho.local` / `Admin123!`.

The canonical local admin entry is `https://localhost:8443/admin/clients`. The `/admin` route redirects there.

### Local Customer Portal And Licensing Overlay

When you want customer onboarding and license requests to stay inside `MrWhoOidc.Web`, start the licensing overlay on top of the base dev stack:

```bash
docker compose -f docker-compose.dev.yml -f docker-compose.licensing-portal.dev.yml up -d --build
```

This overlay adds:
- MrWhoLicensing API at `https://localhost:7443`
- `MrWhoOidc.Web` customer portal at `http://localhost:8088/portal.html`
- seeded OIDC clients from `dev/portal-seed-manifest.json`:
	- `portal-web` for the browser PKCE flow
	- `licensing-admin` for the internal backoffice

Local flow:
- open `http://localhost:8088/portal.html`
- register or sign in through `https://localhost:8443/t/default`
- onboard an organization in the portal
- submit registration or licensing requests through the authenticated portal API

The portal keeps the browser on the `MrWhoOidc.Web` host and proxies:
- `/oidc/*` to WebAuth
- `/licensing/*` to MrWhoLicensing

Customer-safe portal endpoints live under `https://localhost:7443/api/portal/*`.

For an IDE-first workflow, you can also run the Aspire host:

```bash
dotnet run --project MrWhoOidc.AppHost
```

Use `docker-compose.yml` for production-oriented container deployment. Fresh production environments require an explicit `/bootstrap` call guarded by `BOOTSTRAP_TOKEN`; see the production guides below.

## Highlights

### Core OIDC/OAuth 2.0
- OpenID Connect Provider (OP) with full discovery support
- Authorization Code Flow with PKCE
- Client Credentials Grant
- Token Exchange (RFC 8693) with DPoP support
- Back-Channel Logout (BCL) with durable outbox pattern
- JWT signing with key rotation
- Automatic EF Core migrations

### Enterprise-Ready
- **Multi-Tenancy**: Isolated data per tenant with subdomain/path routing
- **High Performance**: Optional Redis caching (60-80% DB load reduction)
- **Production Hardened**: Non-root containers, read-only volumes, network isolation
- **Observability**: Structured logging, OpenTelemetry, health endpoints
- **Zero-Downtime Upgrades**: Backward-compatible migrations, graceful degradation

### Identity Provider Chaining
- Federated authentication with upstream IdPs
- Multi-level IdP configuration support
- Token exchange for delegated access

## Run Modes

- `docker-compose.dev.yml`: primary local development path with seeded data, example applications, MailHog, and source builds.
- `MrWhoOidc.AppHost`: optional Aspire workflow for local .NET debugging and orchestration.
- `docker-compose.yml`: production-oriented container deployment from the published image, with explicit bootstrap and externalized configuration.

## Sample Applications

| Sample | Purpose | Local URL | Notes |
|--------|---------|-----------|-------|
| `Examples/MrWhoOidc.OidcDemo` | Minimal Razor Pages OIDC client | `https://localhost:5001` | Included in `docker-compose.dev.yml` |
| `Examples/MrWhoOidc.RazorClient` | Interactive .NET client with OBO flow to TestApi | `https://localhost:5003` | Included in `docker-compose.dev.yml` and AppHost |
| `Examples/MrWhoOidc.TestApi` | Protected downstream API used by the demos | `https://localhost:7149` | Included in `docker-compose.dev.yml` and AppHost |
| `Examples/ReactOidcClient` | SPA example using PAR and PKCE | `http://localhost:5173` | Included in `docker-compose.dev.yml` |
| `Examples/MrWhoOidc.GoWebClient` | Go web app with auth code + PKCE and optional OBO | Manual run | Uses JSON config |
| `Examples/MrWhoOidc.GoApi` | Go API validating access tokens | Manual run | Uses JSON config |

See [docs/example-applications-guide.md](docs/example-applications-guide.md) for setup details, when to use each example, and how they map to the dev stack.

## Documentation

### Start Here
- [docs/for-developers/quickstart-15-min.md](docs/for-developers/quickstart-15-min.md) - Local development with the seeded Docker stack
- [docs/troubleshooting/local-development.md](docs/troubleshooting/local-development.md) - Common local startup, port, certificate, and Docker issues
- [docs/index.md](docs/index.md) - Documentation hub by audience and workflow
- [docs/production-setup-guide.md](docs/production-setup-guide.md) - Production bootstrap, environment variables, and cloud deployment notes
- [docs/deployment-guide.md](docs/deployment-guide.md) - Full deployment lifecycle and container operations

### Development and Integration
- [docs/developer-guide.md](docs/developer-guide.md) - Integration guide for discovery, authorization, token exchange, JAR/JARM, and DPoP
- [docs/example-applications-guide.md](docs/example-applications-guide.md) - Demo applications and sample architecture map
- [e2e/README.md](e2e/README.md) - Python + Playwright browser E2E suite
- [MrWhoOidc.Cli/README.md](MrWhoOidc.Cli/README.md) - CLI installation and usage (`dotnet tool install --global MrWhoOidc.Cli`; [NuGet](https://www.nuget.org/packages/MrWhoOidc.Cli))

### Operations and Security
- [docs/admin-guide.md](docs/admin-guide.md) - Admin UI and operational workflows
- [docs/docker-compose-examples.md](docs/docker-compose-examples.md) - Deployment variants
- [docs/upgrade-guide.md](docs/upgrade-guide.md) - Upgrade and rollback procedures
- [docs/docker-security-best-practices.md](docs/docker-security-best-practices.md) - Container and runtime hardening

## Testing

- Unit and integration tests: `dotnet test`
- Browser E2E tests: `cd e2e && sh ./setup-venv.sh && .venv/bin/pytest -v`
- Example application health paths are covered by the dev stack and E2E suite

## Container Images

Published images are available at `ghcr.io/popicka70/mrwhooidc`.

```bash
docker pull ghcr.io/popicka70/mrwhooidc:latest

docker pull ghcr.io/popicka70/mrwhooidc:v1.0.0
```