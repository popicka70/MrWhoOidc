# Local Development Troubleshooting

Use this guide when the seeded Docker development stack does not come up cleanly.

Run these commands from the source repository root with Docker Compose V2. Shell examples use Bash; see the [PowerShell setup](../for-developers/quickstart-15-min.md#windows-source-build-powershell-7) for Windows. `curl -k` below is limited to local certificate checks.

## Quick Checks

```bash
docker compose -f docker-compose.dev.yml ps
docker compose -f docker-compose.dev.yml logs --tail=100 webauth
curl -k https://localhost:8443/t/default/.well-known/openid-configuration
bash ./scripts/verify-installation.sh
```

The first successful smoke test is the tenant-scoped discovery document at `https://localhost:8443/t/default/.well-known/openid-configuration`.

## Docker Is Not Running

Symptoms:

- `Cannot connect to the Docker daemon`
- `docker compose` fails before any container starts

Fix:

```bash
docker ps
```

Start Docker Desktop or the Docker daemon, then retry the development stack:

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

## Port Conflict

Typical ports used by the seeded stack:

- `8443` for WebAuth
- `5001` for OidcDemo
- `5003` for RazorClient
- `5173` for ReactOidcClient
- `7149` for TestApi
- `8025` for MailHog

Find the conflicting process:

```bash
sudo lsof -i :8443
sudo lsof -i :5001
sudo lsof -i :5003
sudo lsof -i :5173
sudo lsof -i :7149
```

Stop the conflicting process or change the published port in the relevant compose file before retrying.

On Windows, identify listeners with PowerShell before deciding what to stop:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8443,5001,5003,5173,7149,8025 -ErrorAction SilentlyContinue |
 Select-Object LocalAddress,LocalPort,OwningProcess
```

If you change public ports, update issuer URLs and client redirect URIs together. Do not stop an unrelated service just to free a port.

## First Startup Is Slow

The initial run builds multiple images and waits for PostgreSQL migrations and health checks. Duration depends on image downloads, build cache, disk speed, and database state.

Re-check status before treating the startup as failed:

```bash
docker compose -f docker-compose.dev.yml ps
docker compose -f docker-compose.dev.yml logs --tail=100 postgres
docker compose -f docker-compose.dev.yml logs --tail=100 webauth
```

## Certificate Warning in Browser

The local stack uses a development certificate. Browsers may show a certificate warning the first time you open `https://localhost:8443`.

Use the repository's [certificate setup](../../certs/README.md) to export and trust a certificate. The scripts attempt local trust, but may require confirmation or additional OS/browser steps. They preserve `.env` and regenerate the PFX.

## Configuration Changes Have No Effect

The development Compose file reads `DEV_POSTGRES_PASSWORD`, `DEV_CERT_PASSWORD`, and `DEV_MAIL_*`, not the similarly named production inputs. Check its environment mappings before changing `.env`.

Apply environment changes with `docker compose -f docker-compose.dev.yml up -d`; `restart` alone reuses the old container environment. An existing PostgreSQL role password and an already seeded administrator password are not reset by changing environment variables.

## Discovery Endpoint Fails

If discovery does not load, verify that WebAuth is actually serving the tenant-scoped issuer:

```bash
curl -k https://localhost:8443/t/default/.well-known/openid-configuration
curl -k https://localhost:8443/health
```

Expected discovery fields include:

- `issuer`
- `authorization_endpoint`
- `token_endpoint`
- `jwks_uri`

If `curl` still fails, inspect container logs:

```bash
docker compose -f docker-compose.dev.yml logs --tail=200 webauth
```

## Example App Does Not Start

Check the specific app container instead of only the auth server:

```bash
docker compose -f docker-compose.dev.yml logs --tail=100 oidcdemo
docker compose -f docker-compose.dev.yml logs --tail=100 razorclient
docker compose -f docker-compose.dev.yml logs --tail=100 reactclient
docker compose -f docker-compose.dev.yml logs --tail=100 testapi
```

The auth server can be healthy while an example app still has a build or certificate issue.

## Reset Disposable Development Data

Only use this when all data in this development stack can be deleted. It removes the database and other named volumes, including saved tenants, users, and clients. Back up anything you need first. To stop without deleting volumes, use `down` without `-v`.

```bash
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d --build
```

The next startup creates and seeds a new development database.
