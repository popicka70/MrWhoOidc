# Example Applications Guide

This guide maps the sample applications in the repository to the current local development workflows.

## Overview

MrWhoOidc ships with several example applications that demonstrate different client and API integration patterns.

| Sample | Technology | Scenario | Local URL | Started By |
|--------|------------|----------|-----------|------------|
| `Examples/MrWhoOidc.OidcDemo` | ASP.NET Core Razor Pages | Minimal OIDC sign-in sample | `https://localhost:5001` | `docker-compose.dev.yml` |
| `Examples/MrWhoOidc.RazorClient` | ASP.NET Core Razor Pages | Interactive web app plus on-behalf-of call to TestApi | `https://localhost:5003` | `docker-compose.dev.yml`, `MrWhoOidc.AppHost` |
| `Examples/MrWhoOidc.TestApi` | ASP.NET Core minimal API | Protected downstream API validating bearer tokens and OBO claims | `https://localhost:7149` | `docker-compose.dev.yml`, `MrWhoOidc.AppHost` |
| `Examples/ReactOidcClient` | React + Vite + TypeScript | SPA using PAR, PKCE, and front-channel logout | `http://localhost:5173` | `docker-compose.dev.yml` |
| `Examples/MrWhoOidc.GoWebClient` | Go | Web app using auth code + PKCE with optional OBO | manual run | manual |
| `Examples/MrWhoOidc.GoApi` | Go | API validating tokens issued by MrWhoOidc | manual run | manual |

## Recommended Starting Points

- Use `MrWhoOidc.RazorClient` plus `MrWhoOidc.TestApi` if you want the primary end-to-end .NET demo.
- Use `MrWhoOidc.OidcDemo` if you want the smallest Razor Pages example.
- Use `ReactOidcClient` if you want a browser-only SPA example.
- Use the Go examples if you need a non-.NET integration reference.

## Seeded Local Development Stack

The main local workflow is `docker-compose.dev.yml`:

```bash
cp .env.example .env
docker compose -f docker-compose.dev.yml up -d --build
```

That stack starts the auth server, supporting services, and the dockerized example applications listed above.

The development auth server auto-seeds the default tenant on first request. The examples in the dev stack target:

- issuer: `https://localhost:8443/t/default`
- discovery: `https://localhost:8443/t/default/.well-known/openid-configuration`

Development credentials:

- username: `admin@mrwho.local`
- password: `Admin123!`

## Aspire AppHost Workflow

For local .NET debugging, you can run:

```bash
dotnet run --project MrWhoOidc.AppHost
```

This starts the core auth server and the primary .NET demo pair:

- `MrWhoOidc.WebAuth`
- `MrWhoOidc.TestApi`
- `MrWhoOidc.RazorClient`

If you also need `OidcDemo`, `ReactOidcClient`, or the Go examples, use the dev compose stack or run those applications separately.

## Example Notes

### MrWhoOidc.OidcDemo

- Minimal Razor Pages client for sign-in and logout.
- Included in `docker-compose.dev.yml` at `https://localhost:5001`.
- Good for a small interactive-client reference.

### MrWhoOidc.RazorClient

- Shows interactive login, token display, and on-behalf-of access to `MrWhoOidc.TestApi`.
- Included in both the dev compose stack and AppHost.
- Best choice for understanding the main .NET client library integration.

### MrWhoOidc.TestApi

- Protected downstream API used by the Razor demo and E2E coverage.
- Included in both the dev compose stack and AppHost.
- Good reference for audience validation and delegated `act` claim handling.

### ReactOidcClient

- SPA sample using PAR, PKCE, and front-channel logout.
- Included in `docker-compose.dev.yml` at `http://localhost:5173`.
- Good choice if you need a browser-only OIDC integration reference.

### GoWebClient and GoApi

- Manual-run examples for non-.NET integrations.
- Copy their JSON config templates, then point them at your running issuer.
- The easiest local target is the seeded dev compose stack at `https://localhost:8443/t/default`.

## Related Documentation

- [for-developers/quickstart-15-min.md](for-developers/quickstart-15-min.md)
- [developer-guide.md](developer-guide.md)
- [../e2e/README.md](../e2e/README.md)
- [../MrWhoOidc.Cli/README.md](../MrWhoOidc.Cli/README.md)