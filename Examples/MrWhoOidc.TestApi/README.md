# MrWhoOidc.TestApi

Minimal Web API that validates on-behalf-of access tokens issued by `MrWhoOidc.WebAuth` using the `MrWhoOidc.Client` NuGet package.

## Endpoints

- `GET /me` – requires a bearer access token. Returns subject metadata, granted scopes, and the delegating client (from the `act` claim) to demonstrate OBO.
- `GET /health` – liveness probe for Aspire.

## Configuration

The API is configured through the `MrWhoOidc` section in `appsettings.json`:

- `Issuer` – base URL of the MrWhoOidc authorization server.
- `DiscoveryUri` – tenant-scoped discovery document for the current issuer.
- `ClientId`/`ClientSecret` – confidential client used for policy decisions (and future introspection examples).
- `Audience` – expected `aud` claim for incoming tokens (`api` by default).

At startup the API bootstraps the discovery and JWKS caches provided by `MrWhoOidc.Client`, and the JWT bearer handler resolves signing keys through the shared `IMrWhoJwksCache`.

## Recommended Local Workflows

### Seeded Docker Stack

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

The dev compose stack runs the API at `https://localhost:7149` and points it at the seeded default tenant on `https://localhost:8443/t/default`.

## Running with Aspire

The API is wired into `MrWhoOidc.AppHost` so `dotnet run --project MrWhoOidc.AppHost` will launch the auth server, Razor client, and this API together. The Razor client uses the client’s on-behalf-of helpers to acquire tokens for this API automatically.

If you run the API standalone, keep the `MrWhoOidc` settings aligned with your issuer and discovery document.
