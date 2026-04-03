# Local Development Troubleshooting

Use this guide when the seeded Docker development stack does not come up cleanly.

The commands below use `docker compose` (Compose V2). If your machine only has `docker-compose`, substitute that command name.

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

## First Startup Is Slow

The initial run builds multiple images and waits for PostgreSQL migrations and health checks. That can take 60-120 seconds on a clean machine.

Re-check status before treating the startup as failed:

```bash
docker compose -f docker-compose.dev.yml ps
docker compose -f docker-compose.dev.yml logs --tail=100 postgres
docker compose -f docker-compose.dev.yml logs --tail=100 webauth
```

## Certificate Warning in Browser

The local stack uses a development certificate. Browsers may show a certificate warning the first time you open `https://localhost:8443`.

That is expected for local development. If you need a trusted local certificate, generate and trust one separately for your machine.

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

## Need a Clean Slate

```bash
docker compose -f docker-compose.dev.yml down -v
docker compose -f docker-compose.dev.yml up -d --build
```

This removes the local database volume and rebuilds the seeded environment from scratch.
