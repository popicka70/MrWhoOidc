# MrWhoOidc Getting Started

Use the published Docker image first. Build from source only if you are contributing to MrWhoOidc itself.

Choose one path and stay on it:
- Path 1: clone `MrWho` and run the published Docker image. This is the default and recommended path.
- Path 2: clone `MrWhoOidc` and build local images from source. Use this only when you need to change code or debug the product.

Do not clone either repository into `/tmp` or another temporary directory. Use a persistent directory such as `$HOME/src`.

The commands below use `docker compose` (Compose V2). If your environment only has `docker-compose`, substitute that command name.

## Path 1: Published Docker Image From `MrWho` (Recommended)

### Use this path when

- You want the default installation path.
- You want to test or deploy the published image.
- You do not need to change the MrWhoOidc source code.

### Prerequisites

- Docker Engine 20.10+ and Docker Compose V2+
- Git
- OpenSSL
- 4 GB RAM recommended

### Step 1: Create a persistent working directory

Run these commands exactly:

```bash
mkdir -p "$HOME/src"
cd "$HOME/src"
pwd
```

Expected result:
- `pwd` prints a path under your home directory, for example `/home/your-user/src`.
- Do not continue if the path is `/tmp`, `/var/tmp`, or another temporary directory.

### Step 2: Clone the public deployment repository

Run these commands exactly:

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

Run these commands exactly:

```bash
bash ./scripts/generate-cert.sh localhost changeit
test -f ./certs/aspnetapp.pfx && echo "certificate ready"
```

Expected result:
- The final command prints `certificate ready`.

### Step 4: Create `.env`

Run this command:

```bash
cp .env.example .env
```

Open `.env` and set these values for a first local run:

```dotenv
POSTGRES_PASSWORD=ChangeMeNow123!
OIDC_PUBLIC_BASE_URL=https://localhost:8443
CERT_PASSWORD=changeit
BOOTSTRAP_TOKEN=temporary-bootstrap-token
```

Notes:
- Keep `CERT_PASSWORD=changeit` because it must match the certificate generated in Step 3.
- If you are reusing an existing database that already has a tenant and admin user, leave `BOOTSTRAP_TOKEN=` empty and skip Step 6.

### Step 5: Start the published-image stack

Run these commands exactly:

```bash
docker compose up -d
docker compose ps
```

Expected result:
- Compose starts `mrwho-oidc` and `mrwho-postgres`.
- The first run may take a few minutes because Docker needs to pull images and PostgreSQL needs to initialize.

### Step 6: Bootstrap the first tenant on an empty database

Run this step only if the database is empty and you set `BOOTSTRAP_TOKEN` in Step 4.

Run this command exactly:

```bash
curl -k -X POST https://localhost:8443/bootstrap \
  -H 'Content-Type: application/json' \
  -H 'X-Bootstrap-Token: temporary-bootstrap-token' \
  -d '{
    "tenantSlug": "default",
    "tenantName": "Default Tenant",
    "adminEmail": "admin@example.com",
    "adminPassword": "ChangeMeNow123!",
    "adminName": "Administrator"
  }'
```

If the first request fails immediately after `docker compose up -d`, wait 10 seconds and run the same command again. The first HTTPS bind can take a moment.

### Step 7: Disable bootstrap and restart

After Step 6 succeeds:
- Open `.env`.
- Change `BOOTSTRAP_TOKEN=temporary-bootstrap-token` to `BOOTSTRAP_TOKEN=`.
- Save the file.

Then run:

```bash
docker compose up -d
```

### Step 8: Verify the installation

Run these commands exactly:

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

### Use this path when

- You need to modify the product code.
- You want the seeded development stack and sample applications.
- You want Docker to build local images from the checked-out source tree.

### Prerequisites

- Docker Engine 20.10+ and Docker Compose V2+
- Git
- 4 GB RAM recommended and 5 GB free disk space preferred
- .NET 10 SDK only if you also want AppHost, tests, or local IDE debugging

### Step 1: Create a persistent working directory

Run these commands exactly:

```bash
mkdir -p "$HOME/src"
cd "$HOME/src"
pwd
```

Expected result:
- `pwd` prints a path under your home directory.
- Do not continue if the path is `/tmp`, `/var/tmp`, or another temporary directory.

### Step 2: Clone the source repository

Run these commands exactly:

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

### Step 3: Create `.env`

Run this command:

```bash
cp .env.example .env
```

For the first source-built local run, the default development values are usually enough. Edit `.env` only if you need to override defaults.

### Step 4: Build and start the development stack

Run these commands exactly:

```bash
docker compose -f docker-compose.dev.yml up -d --build
docker compose -f docker-compose.dev.yml ps
```

Expected result:
- Docker builds local images from this repository.
- Compose starts WebAuth, PostgreSQL, Redis, MailHog, and the sample applications.
- The first run can take a few minutes because multiple images are built locally.

### Step 5: Verify the seeded development tenant

Run these commands exactly:

```bash
curl -k https://localhost:8443/t/default/.well-known/openid-configuration
bash ./scripts/verify-installation.sh
```

Expected result:
- The discovery document returns JSON.
- The verification script completes without reporting a failure.

### Step 6: Sign in to the seeded development tenant

Use these development-only credentials:

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

## Common Mistakes And Fixes

### Mistake: Running commands from the wrong directory

Fix:
- For Path 1, run `pwd` and confirm the current directory ends with `/MrWho`.
- For Path 2, run `pwd` and confirm the current directory ends with `/MrWhoOidc`.
- If the path is wrong, stop and `cd` to the correct repository root before running any more commands.

### Mistake: Cloning into `/tmp`

Fix:
- Delete the temporary clone.
- Re-clone into a persistent directory such as `$HOME/src`.
- Start again from Step 1 of the path you chose.

### Mistake: Mixing Path 1 and Path 2 commands

Fix:
- Path 1 uses the `MrWho` repository and `docker compose up -d`.
- Path 2 uses the `MrWhoOidc` repository and `docker compose -f docker-compose.dev.yml up -d --build`.
- If you mixed them, stop the containers, move to the correct repository, and restart with the correct command set.

### Mistake: Calling `/bootstrap` on the source-built development stack

Fix:
- Do not call `/bootstrap` for Path 2.
- Path 2 auto-seeds the default tenant and admin account.
- Use `/bootstrap` only for Path 1 on an empty database.

### Mistake: Port 8443 or demo ports are already in use

Fix:

```bash
sudo lsof -i :8443
sudo lsof -i :5001
sudo lsof -i :5003
sudo lsof -i :5173
sudo lsof -i :7149
```

Stop the conflicting process, then rerun the startup command for your chosen path.

### Mistake: First startup looks slow

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

### Remove Path 2 local data for a clean slate

Run this command from `$HOME/src/MrWhoOidc`:

```bash
docker compose -f docker-compose.dev.yml down -v
```

Warning: this deletes the local development database and other dev-stack volumes.

## Getting Help

- Documentation hub: [../index.md](../index.md)
- Issues: https://github.com/popicka70/MrWhoOidc/issues
- Discussions: https://github.com/popicka70/MrWhoOidc/discussions

## Quick Reference

| Path | Task | Command |
|------|------|---------|
| Path 1 | Start services | `docker compose up -d` |
| Path 1 | Verify discovery | `curl -k https://localhost:8443/t/default/.well-known/openid-configuration` |
| Path 1 | Bootstrap empty database | `curl -k -X POST https://localhost:8443/bootstrap ...` |
| Path 2 | Start services | `docker compose -f docker-compose.dev.yml up -d --build` |
| Path 2 | Verify discovery | `curl -k https://localhost:8443/t/default/.well-known/openid-configuration` |
| Path 2 | Full verification | `bash ./scripts/verify-installation.sh` |

**Last Updated:** 2026-05-15
