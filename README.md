# MrWhoOidc

[![Docker Image](https://img.shields.io/badge/docker-ghcr.io%2Fpopicka70%2Fmrwhooidc-blue)](https://ghcr.io/popicka70/mrwhooidc)
[![Multi-Arch](https://img.shields.io/badge/arch-amd64%20%7C%20arm64-informational)](https://ghcr.io/popicka70/mrwhooidc)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

An OpenID Connect (OIDC) and OAuth 2.0 provider built on .NET 10, with PostgreSQL, optional Redis caching, a tenant-aware admin UI, sample applications, and browser E2E tests.

For OpenID Foundation conformance test reruns, see the [runbook](tools/certification/README.md) and [readiness assessment](docs/oidc-openid-certification-readiness.md). Test reports do not by themselves establish OpenID certification.

Source code in this repository is licensed under Apache 2.0. Use of the `MrWhoOidc` name, logos, and other brand assets is governed separately by [TRADEMARK_POLICY.md](TRADEMARK_POLICY.md).

## Getting Started

Use Docker Compose V2 (`docker compose`). Shell examples below use Bash; the [getting-started guide](docs/for-developers/quickstart-15-min.md) also covers Windows setup. Keep `curl -k` and development certificates limited to local testing.

If you just want to run MrWhoOidc, clone `MrWho` and use the published image (Option 1). Clone this repository only if you need to change the code, debug it, or build the images yourself (Option 2).

### Option 1: Run The Published Docker Image From `MrWho` (Recommended)

This path pulls `ghcr.io/popicka70/mrwhooidc:latest`; it does not build the application locally. For production, select a release tag or digest and follow the [production setup guide](docs/production-setup-guide.md), including DataProtection certificate configuration, before starting the service.

Use a persistent working directory. Do not clone into `/tmp`.

```bash
mkdir -p "$HOME/src"
cd "$HOME/src"
pwd

git clone https://github.com/popicka70/MrWho.git
cd "$HOME/src/MrWho"
pwd

test -f docker-compose.yml && test -f .env.example && echo "MrWho repo ready"

# Local evaluation certificate (Bash + OpenSSL)
bash scripts/generate-cert.sh localhost changeit
chmod 644 certs/aspnetapp.pfx
cp .env.example .env
```

Follow the [deployment repository's setup instructions](https://github.com/popicka70/MrWho#readme) for its certificate and environment prerequisites. The local certificate must be at `certs/aspnetapp.pfx`, and `CERT_PASSWORD` must match its password. Do not overwrite an existing `.env` or certificate without checking which services use it.

For local evaluation only, edit `.env` and set:

- `ASPNETCORE_ENVIRONMENT=Development` (the default Production environment rejects the local certificate password)
- `POSTGRES_PASSWORD` to a generated password
- `CERT_PASSWORD=changeit` (matches the locally generated certificate)
- `OIDC_PUBLIC_BASE_URL=https://localhost:8443`
- `BOOTSTRAP_TOKEN` to a temporary secret if the database is empty

Keep this configuration on a local test machine, not an Internet-facing host. Development alone does not enable automatic seed data in the current application, so this path still uses explicit bootstrap. For production, follow the separate guide and check that the deployment Compose file actually forwards every required DataProtection setting; adding it only to `.env` is insufficient.

Then start and bootstrap the stack:

Replace the bootstrap token and administrator credentials in the example. Compose reads `.env`, but does not export those values into the calling shell.

```bash
docker compose up -d

curl -k -X POST https://localhost:8443/bootstrap \
 -H 'Content-Type: application/json' \
 -H 'X-Bootstrap-Token: your-temporary-bootstrap-token' \
 -d '{
  "tenantSlug": "default",
  "tenantName": "Default Tenant",
  "adminEmail": "admin@example.com",
  "adminPassword": "<unique-administrator-password>",
  "adminName": "Administrator"
 }'

# Remove BOOTSTRAP_TOKEN from .env after the bootstrap succeeds.
docker compose up -d

curl -k https://localhost:8443/t/default/.well-known/openid-configuration
bash ./scripts/health-check.sh https://localhost:8443 default
```

This option runs the published image as-is, with no local builds.

### Option 2: Build From Source From This Repository

Choose this option only if you need to modify or debug MrWhoOidc itself. The commands below build local images from the source tree you just checked out.

All commands assume your current directory is the repository root. Use a persistent folder such as `$HOME/src`, not `/tmp`.

Install the .NET 10 SDK for the certificate setup script, plus Docker Compose V2 and Git. The application build itself runs inside Docker.

```bash
mkdir -p "$HOME/src"
cd "$HOME/src"
pwd

git clone https://github.com/popicka70/MrWhoOidc.git
cd "$HOME/src/MrWhoOidc"
pwd

test -f docker-compose.dev.yml && test -f .env.example && echo "MrWhoOidc source repo ready"

# Run the dev-setup script to generate a local HTTPS certificate and .env file
# (generates certs/aspnetapp.pfx, trusts it, and creates .env from .env.example)
bash scripts/setup-dev.sh          # Linux/macOS
# pwsh scripts/setup-dev.ps1       # Windows

docker compose -f docker-compose.dev.yml up -d --build

curl -k https://localhost:8443/t/default/.well-known/openid-configuration
bash ./scripts/verify-installation.sh
```

> **Development configuration:** `docker-compose.dev.yml` reads `DEV_POSTGRES_PASSWORD`, `DEV_CERT_PASSWORD`, and `DEV_MAIL_*`, not the `POSTGRES_PASSWORD` / `CERT_PASSWORD` values generated by setup. The certificate default is `changeit`. The stack uses known credentials and relaxed test settings; do not expose it to the Internet.

The source-built development stack starts:

- MrWhoOidc WebAuth at `https://localhost:8443`
- PostgreSQL and Redis
- MailHog at `http://localhost:8025`
- OidcDemo at `https://localhost:5001`
- RazorClient at `https://localhost:5003`
- ReactOidcClient at `http://localhost:5173`
- TestApi at `https://localhost:7149`

> **Resetting state:** `docker compose -f docker-compose.dev.yml down` stops containers but keeps the PostgreSQL/Redis volumes, so tenant data and seeded accounts persist across restarts. To wipe all state and re-seed from scratch, add `-v`: `docker compose -f docker-compose.dev.yml down -v`.

This Compose stack explicitly enables development seed data. Sign in with `admin@mrwho.local` / `Admin123!` unless you changed `SEED_ADMIN_PASSWORD` before the account was created. Changing that variable does not reset an existing account's password.

The local admin UI lives at `https://localhost:8443/admin/clients`; the `/admin` route redirects there.

#### Local Customer Portal And Licensing Overlay

To keep customer onboarding and license requests inside `MrWhoOidc.Web`, start the licensing overlay on top of the dev stack:

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

If you prefer working from an IDE, you can also run the Aspire host:

```bash
dotnet run --project MrWhoOidc.AppHost
```

For a production-style deployment from the published image, use Option 1 above and the `MrWho` repository.

## Highlights

### Core OIDC/OAuth 2.0

- OpenID Connect discovery and JWKS endpoints
- Authorization Code Flow with PKCE
- Client Credentials Grant
- Token Exchange (RFC 8693) with DPoP support
- Back-Channel Logout (BCL) with durable outbox pattern
- JWT signing with key rotation
- Automatic EF Core migrations

### Operations

- **Multi-Tenancy**: Isolated data per tenant with subdomain/path routing
- **User Enrollment**: Self-service registration, tenant invitations, and tenant domain auto-join
- **Optional Redis caching**: Configure `ConnectionStrings__redis`; see the [configuration reference](docs/production-setup-guide.md#optional-features) for Compose wiring
- **Container configuration**: Certificate mounts, service networks, and persistent database volumes are defined in the Compose files
- **Observability**: Structured logging, OpenTelemetry, health endpoints
- **Upgrades**: Database migrations run at startup; back up and test recovery before updating

### Identity Provider Chaining

- Federated authentication with upstream IdPs
- Multi-level IdP configuration support
- Token exchange for delegated access

## Run Modes

- `MrWho` repository `docker-compose.yml`: recommended published-image path for first-time local and production-style deployment.
- `docker-compose.dev.yml`: source-build dev stack — seeded data, example applications, MailHog, and local image builds. Use this for modifying or debugging MrWhoOidc (Option 2 above).
- `docker-compose.yml` (in this repo): source-repo production-shaped compose file that builds locally from this repository's Dockerfile. Use this only when you need a production-shaped layout from source; for everyday dev work prefer `docker-compose.dev.yml`.
- `MrWhoOidc.AppHost`: optional Aspire workflow for local .NET debugging and orchestration.

## Security Configuration

Two security-sensitive settings can be configured explicitly:

```json
{
  "Auth": {
    "TokenValidationClockSkewSeconds": 60
  },
  "KeyRotation": {
    "RsaKeySizeBits": 3072
  }
}
```

- `Auth:TokenValidationClockSkewSeconds` controls JWT lifetime clock skew for local token validation.
- `KeyRotation:RsaKeySizeBits` controls the size of newly generated RSA signing and encryption keys.
- If you increase `KeyRotation:RsaKeySizeBits` above the active RSA signing key size, the next key rotation check will mint a replacement signing key immediately and keep the old signing key published until the configured overlap window expires.

For environment-variable based deployments, use `Auth__TokenValidationClockSkewSeconds` and `KeyRotation__RsaKeySizeBits`.

## Sample Applications

| Sample                           | Purpose                                           | Local URL                | Notes                                            |
| -------------------------------- | ------------------------------------------------- | ------------------------ | ------------------------------------------------ |
| `Examples/MrWhoOidc.OidcDemo`    | Minimal Razor Pages OIDC client                   | `https://localhost:5001` | Included in `docker-compose.dev.yml`             |
| `Examples/MrWhoOidc.RazorClient` | Interactive .NET client with OBO flow to TestApi  | `https://localhost:5003` | Included in `docker-compose.dev.yml` and AppHost |
| `Examples/MrWhoOidc.TestApi`     | Protected downstream API used by the demos        | `https://localhost:7149` | Included in `docker-compose.dev.yml` and AppHost |
| `Examples/ReactOidcClient`       | SPA example using PAR and PKCE                    | `http://localhost:5173`  | Included in `docker-compose.dev.yml`             |
| `Examples/MrWhoOidc.GoWebClient` | Go web app with auth code + PKCE and optional OBO | Manual run               | Uses JSON config                                 |
| `Examples/MrWhoOidc.GoApi`       | Go API validating access tokens                   | Manual run               | Uses JSON config                                 |

See [docs/example-applications-guide.md](docs/example-applications-guide.md) for setup details, when to use each example, and how they map to the dev stack.

## Documentation

### Start Here

- [docs/for-developers/quickstart-15-min.md](docs/for-developers/quickstart-15-min.md) - Getting started with the published image first and source builds second
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
- [docs/user-registration-and-enrollment.md](docs/user-registration-and-enrollment.md) - Registration, invitations, and tenant domain claims
- [docs/docker-compose-examples.md](docs/docker-compose-examples.md) - Deployment variants
- [docs/upgrade-guide.md](docs/upgrade-guide.md) - Upgrade and rollback procedures
- [docs/docker-security-best-practices.md](docs/docker-security-best-practices.md) - Container and runtime hardening

## Testing

- Unit and integration tests: `dotnet test`
- Browser E2E tests: follow [e2e/README.md](e2e/README.md) for the virtual environment, running services, browser installation, and test configuration
- Example application health paths are covered by the dev stack and E2E suite

## Container Images

Published images are available at `ghcr.io/popicka70/mrwhooidc`.

```bash
docker pull ghcr.io/popicka70/mrwhooidc:latest
```

For production, choose an available release tag or digest from the registry rather than relying on the moving `latest` tag. Image size and startup time depend on the selected build and deployment environment.
