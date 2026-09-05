# MrWhoOidc Getting Started

Use the published Docker image to evaluate or deploy the service. Build from source to change the code, debug it, or run the development examples.

The two repositories use different Compose files and initialization steps:

- Path 1: clone `MrWho` and run the published Docker image. This is the default and recommended path.
- Path 2: clone `MrWhoOidc` and build local images from source. Use this only when you need to change code or debug the product.

Use a persistent directory such as `$HOME/src` so configuration and certificates survive temporary-directory cleanup.

The shell examples below use Bash and Docker Compose V2 (`docker compose`). A complete [Windows source-build sequence](#windows-source-build-powershell-7) appears below. Bash commands are not interchangeable with PowerShell commands. Upgrade older Compose installations to V2 before following this guide.

## Path 1: Published Docker Image From `MrWho` (Recommended)

### When to use the published image

- You want the default installation path.
- You want to test or deploy the published image.
- You do not need to change the MrWhoOidc source code.

### Published-image prerequisites

- Docker Engine 20.10+ and Docker Compose V2+
- Git
- OpenSSL
- 4 GB RAM recommended

### Step 1: Create a persistent working directory

```bash
mkdir -p "$HOME/src"
cd "$HOME/src"
pwd
```

`pwd` should show your persistent working directory, for example `/home/your-user/src`.

### Step 2: Clone the public deployment repository

```bash
git clone https://github.com/popicka70/MrWho.git
cd "$HOME/src/MrWho"
pwd
test -f docker-compose.yml && test -f .env.example && test -x scripts/generate-cert.sh && echo "MrWho repo ready"
```

Expected result:

- `pwd` ends with `/MrWho`.
- The final command prints `MrWho repo ready`.

Run every remaining Path 1 command from `$HOME/src/MrWho`.

### Step 3: Generate the local TLS certificate

```bash
bash ./scripts/generate-cert.sh localhost changeit
chmod 644 ./certs/aspnetapp.pfx
test -f ./certs/aspnetapp.pfx && echo "certificate ready"
```

Expected result:

- The final command prints `certificate ready`.

`scripts/generate-cert.sh` belongs to the external `MrWho` deployment repository. For a `MrWhoOidc` source checkout, follow Path 2 instead; its setup steps differ.

### Local certificate scope

Keep the generated PFX out of version control. The `changeit` password and the examples using `curl -k` are for local evaluation only. For production, use a trusted certificate and keep TLS verification enabled.

### Step 4: Create `.env`

Run this command:

```bash
cp .env.example .env
```

Open `.env` and set these values for a first local run. Replace both secret placeholders with independently generated values; do not use the placeholders as credentials.

```dotenv
ASPNETCORE_ENVIRONMENT=Development
POSTGRES_PASSWORD=<generated-database-password>
OIDC_PUBLIC_BASE_URL=https://localhost:8443
CERT_PASSWORD=changeit
BOOTSTRAP_TOKEN=<generated-bootstrap-token>
```

Notes:

- This is a local evaluation configuration. The deployment repo defaults to Production, where current WebAuth rejects `changeit` and requires DataProtection key-ring encryption. For production, use the [production setup guide](../production-setup-guide.md) and explicitly forward its required application keys through the deployment Compose file; `.env` entries alone are not enough.
- Do not expose this Development configuration to the Internet. It does not enable auto-seeding by itself, so bootstrap is still required in the current application.
- Keep `CERT_PASSWORD=changeit` because it must match the certificate generated in Step 3.
- If you are reusing an existing database that already has a tenant and admin user, leave `BOOTSTRAP_TOKEN=` empty and skip Step 6.

### Step 5: Start the published-image stack

```bash
docker compose up -d
docker compose ps
```

Expected result:

- Compose starts `mrwho-oidc` and `mrwho-postgres`.
- The first run may take a few minutes because Docker needs to pull images and PostgreSQL needs to initialize.

### Step 6: Bootstrap the first tenant on an empty database

Run this step only if the database is empty and you set `BOOTSTRAP_TOKEN` in Step 4.

Replace the token below with the value configured in `.env`, and choose a unique administrator password. Compose reads `.env` for substitution; it does not export its values into your shell.

```bash
curl -k -X POST https://localhost:8443/bootstrap \
  -H 'Content-Type: application/json' \
  -H 'X-Bootstrap-Token: <generated-bootstrap-token>' \
  -d '{
    "tenantSlug": "default",
    "tenantName": "Default Tenant",
    "adminEmail": "admin@example.com",
    "adminPassword": "<unique-administrator-password>",
    "adminName": "Administrator"
  }'
```

If the connection is refused during startup, inspect `docker compose ps` and the container logs before retrying. For an HTTP error response, read its status and body: a token mismatch or an already initialized database is not a startup delay.

### Step 7: Disable bootstrap and restart

After Step 6 succeeds:

- Open `.env`.
- Clear the value of `BOOTSTRAP_TOKEN`.
- Save the file.

Then run:

```bash
docker compose up -d
```

### Step 8: Verify the installation

```bash
curl -k https://localhost:8443/t/default/.well-known/openid-configuration
curl -k -I https://localhost:8443/admin/clients
curl -k https://localhost:8443/t/default/jwks
bash ./scripts/health-check.sh https://localhost:8443 default
```

Expected result:

- The discovery document returns JSON.
- The admin UI request returns an HTTP redirect to the tenant login page.
- The JWKS endpoint returns signing keys.
- The health-check script completes without reporting a failure.

### Default local endpoints for Path 1

- Discovery: `https://localhost:8443/t/default/.well-known/openid-configuration`
- Admin UI: `https://localhost:8443/admin/clients`
- Tenant JWKS: `https://localhost:8443/t/default/jwks`

## Path 2: Build From Source From `MrWhoOidc`

### When to build from source

- You need to modify the product code.
- You want the seeded development stack and sample applications.
- You want Docker to build local images from the checked-out source tree.

### Source-build prerequisites

- Docker Engine 20.10+ and Docker Compose V2+
- Git
- 4 GB RAM recommended and 5 GB free disk space preferred
- .NET 10 SDK for the certificate setup script, AppHost, tests, or local IDE debugging. Docker builds the application inside its build container.

### Step 1: Create a source working directory

```bash
mkdir -p "$HOME/src"
cd "$HOME/src"
pwd
```

`pwd` should show the directory where you want to keep the source checkout.

### Step 2: Clone the source repository

```bash
git clone https://github.com/popicka70/MrWhoOidc.git
cd "$HOME/src/MrWhoOidc"
pwd
test -f docker-compose.dev.yml && test -f .env.example && test -d MrWhoOidc.WebAuth && echo "MrWhoOidc source repo ready"
```

Expected result:

- `pwd` ends with `/MrWhoOidc`.
- The final command prints `MrWhoOidc source repo ready`.

Run every remaining Path 2 command from `$HOME/src/MrWhoOidc`.

### Step 3: Generate the certificate and create `.env`

Run this command from the repository root:

```bash
bash scripts/setup-dev.sh          # Linux/macOS
# pwsh scripts/setup-dev.ps1       # Windows
```

The setup script:

- exports a local HTTPS developer certificate to `certs/aspnetapp.pfx` (password `changeit`) and makes it readable by the container user,
- trusts the certificate so browsers don't warn on `https://localhost:8443`,
- creates `.env` from `.env.example` with development defaults (`CERT_PASSWORD=changeit`, `OIDC_PUBLIC_BASE_URL=https://localhost:8443`, a random `POSTGRES_PASSWORD`, and an empty `BOOTSTRAP_TOKEN`).

The development Compose file reads `DEV_POSTGRES_PASSWORD`, `DEV_CERT_PASSWORD`, and `DEV_MAIL_*` overrides.
The `POSTGRES_PASSWORD` and `CERT_PASSWORD` values generated in `.env` apply to other Compose workflows, not this stack.
Its default `DEV_CERT_PASSWORD` is `changeit`, matching the generated certificate. Check `docker-compose.dev.yml` before adding overrides.
These defaults and seeded accounts are for local development, not an Internet-facing deployment.

> Fallback: if the setup script is unavailable, run `cp .env.example .env` manually and follow the `dotnet dev-certs` steps in [certs/README.md](../../certs/README.md) to generate `certs/aspnetapp.pfx`.

### Source-build certificate handling

The setup script exports a certificate locally and attempts to trust it. Trust may require confirmation or additional OS/browser configuration. It preserves an existing `.env`, but regenerates `certs/aspnetapp.pfx`; keep that file out of version control.

### Step 4: Build and start the development stack

```bash
docker compose -f docker-compose.dev.yml up -d --build
docker compose -f docker-compose.dev.yml ps
```

Expected result:

- Docker builds local images from this repository.
- Compose starts WebAuth, PostgreSQL, Redis, MailHog, and the sample applications.
- The first run can take a few minutes because multiple images are built locally.

### Step 5: Verify the seeded development tenant

```bash
curl -k https://localhost:8443/t/default/.well-known/openid-configuration
bash ./scripts/verify-installation.sh
```

Expected result:

- The discovery document returns JSON.
- The verification script completes without reporting a failure.

### Step 6: Sign in to the seeded development tenant

Use these development-only defaults, unless `SEED_ADMIN_PASSWORD` was changed before the account was created. Changing that variable does not reset an existing account:

- Username: `admin@mrwho.local`
- Password: `Admin123!`

Useful URLs:

- Admin UI: `https://localhost:8443/admin/clients`
- OidcDemo: `https://localhost:5001`
- RazorClient: `https://localhost:5003`
- ReactOidcClient: `http://localhost:5173`
- TestApi health: `https://localhost:7149/health`

The development stack auto-seeds the default tenant. Do not call `/bootstrap` for Path 2.

### Step 7: Optional contributor workflows

If you want the licensing overlay on top of the source-built dev stack, run:

```bash
docker compose -f docker-compose.dev.yml -f docker-compose.licensing-portal.dev.yml up -d --build
```

If you want the IDE-first Aspire workflow instead of the Docker dev stack, run:

```bash
dotnet run --project MrWhoOidc.AppHost
```

### Default local services for Path 2

- MrWhoOidc WebAuth at `https://localhost:8443`
- PostgreSQL and Redis
- MailHog at `http://localhost:8025`
- OidcDemo at `https://localhost:5001`
- RazorClient at `https://localhost:5003`
- ReactOidcClient at `http://localhost:5173`
- TestApi at `https://localhost:7149`

### Important difference between the two paths

- Path 1 pulls the published Docker image from `ghcr.io/popicka70/mrwhooidc`.
- Path 2 builds local Docker images from your checked-out `MrWhoOidc` source tree.
- Path 1 requires a bootstrap step on an empty database.
- Path 2 auto-seeds the development tenant and does not use bootstrap.

Do not mix the commands from Path 1 and Path 2 in the same clone.

## Troubleshooting

### Windows source build (PowerShell 7)

With Docker Desktop running Linux containers, Git, PowerShell 7, and the .NET 10 SDK installed:

```powershell
New-Item -ItemType Directory -Path "$HOME/src" -Force | Out-Null
Set-Location "$HOME/src"
git clone https://github.com/popicka70/MrWhoOidc.git
Set-Location MrWhoOidc
pwsh ./scripts/setup-dev.ps1
docker compose -f docker-compose.dev.yml up -d --build
docker compose -f docker-compose.dev.yml ps
Invoke-RestMethod -Uri https://localhost:8443/t/default/.well-known/openid-configuration -SkipCertificateCheck
```

Accept the local certificate trust prompt if appropriate. `-SkipCertificateCheck` is only for this localhost check. If you already have the checkout, start from its root rather than cloning it again. The optional `verify-installation.sh` helper needs Bash; the discovery request above does not.

### Compose file not found

Fix:

- For Path 1, run `pwd` and confirm the current directory ends with `/MrWho`.
- For Path 2, run `pwd` and confirm the current directory ends with `/MrWhoOidc`.
- If the path is wrong, stop and `cd` to the correct repository root before running any more commands.

### Checkout is in a temporary directory

Move the checkout to a persistent directory before continuing. Preserve any local changes, `.env`, certificates, and bind-mounted data. Check the Compose project name and volume mappings before restarting so you do not accidentally attach a new empty database.

### Unexpected services or image builds

Fix:

- Path 1 uses the `MrWho` repository and `docker compose up -d`.
- Path 2 uses the `MrWhoOidc` repository and `docker compose -f docker-compose.dev.yml up -d --build`.
- If you mixed them, stop the containers, move to the correct repository, and restart with the correct command set.

### Bootstrap reports an existing tenant

Fix:

- Do not call `/bootstrap` for Path 2.
- Path 2 auto-seeds the default tenant and admin account.
- Use `/bootstrap` only for Path 1 on an empty database.

### Port 8443 or demo ports are already in use

Fix:

```bash
sudo lsof -i :8443
sudo lsof -i :5001
sudo lsof -i :5003
sudo lsof -i :5173
sudo lsof -i :7149
```

Identify which application owns the port before stopping anything. Stop it only if it is no longer needed, or choose different bindings and update the issuer and client configuration together.

### Startup takes longer than expected

Fix:

- Path 1 must pull images and initialize PostgreSQL.
- Path 2 must build multiple images locally.
- Wait a little longer, then inspect status:

```bash
# Path 1
docker compose ps

# Path 2
docker compose -f docker-compose.dev.yml ps
docker compose -f docker-compose.dev.yml logs --tail=100 webauth
```

### Need deeper troubleshooting

- For the source-built development stack, use [../troubleshooting/local-development.md](../troubleshooting/local-development.md).
- For production-style container deployment, use [../deployment-guide.md](../deployment-guide.md) and [../production-setup-guide.md](../production-setup-guide.md).

## Next Steps

- Review [../developer-guide.md](../developer-guide.md) for discovery, authorization, token exchange, JAR/JARM, and DPoP details.
- Review [../admin-guide.md](../admin-guide.md) for tenant and client management workflows.
- Review [../example-applications-guide.md](../example-applications-guide.md) if you want to use the sample applications.
- Run `dotnet test` from the source repo if you are contributing code.

## Cleanup

### Stop Path 1 services

Run this command from `$HOME/src/MrWho`:

```bash
docker compose down
```

### Stop Path 2 services

Run this command from `$HOME/src/MrWhoOidc`:

```bash
docker compose -f docker-compose.dev.yml down
```

### Delete disposable development data

Run this command from `$HOME/src/MrWhoOidc`:

```bash
docker compose -f docker-compose.dev.yml down -v
```

Warning: this deletes the local development database and other dev-stack volumes.

## Getting Help

- Documentation hub: [../index.md](../index.md)
- Issues: <https://github.com/popicka70/MrWhoOidc/issues>
- Discussions: <https://github.com/popicka70/MrWhoOidc/discussions>

## Quick Reference

| Path | Task | Command |
| ------ | ------ | --------- |
| Path 1 | Start services | `docker compose up -d` |
| Path 1 | Verify discovery | `curl -k https://localhost:8443/t/default/.well-known/openid-configuration` |
| Path 1 | Bootstrap empty database | `curl -k -X POST https://localhost:8443/bootstrap ...` |
| Path 2 | Start services | `docker compose -f docker-compose.dev.yml up -d --build` |
| Path 2 | Verify discovery | `curl -k https://localhost:8443/t/default/.well-known/openid-configuration` |
| Path 2 | Full verification | `bash ./scripts/verify-installation.sh` |

**Last Updated:** 2026-09-05
