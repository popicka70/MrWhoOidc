# MrWhoOidc.GoApi

Go minimal API that validates access tokens issued by `MrWhoOidc.WebAuth` and exposes a `GET /me` endpoint mirroring the .NET sample.

## Prerequisites

- Go 1.22+
- A running MrWhoOidc issuer (local Aspire host serves one at `https://localhost:7208`)
- Access tokens targeted at the configured audience (default `api`)

## Setup

1. Copy `config.example.json` to `config.json` (or point the `MRWHO_GO_API_CONFIG` environment variable elsewhere).
2. Update the settings:
   - `issuer`: base URL of your MrWhoOidc server.
   - `audience`: expected `aud` claim in incoming access tokens.
   - `trusted_act_clients`: optional allow-list for the `act.client_id` claim emitted during on-behalf-of exchanges.
   - `jwks_refresh`: cadence used to refresh signing keys (defaults to two minutes).
3. Ensure the downstream API client scopes align with what the issuer issues (for example `api.read`).

## Run it

```powershell
cd Examples/MrWhoOidc.GoApi
go run .
```

You can now call:
- `GET http://localhost:5190/health` – liveness probe.
- `GET http://localhost:5190/me` – requires a Bearer token. Returns subject metadata, granted scopes, and the delegated client from the `act` claim when present.

## Implementation details

- Discovery metadata is loaded once at startup using `github.com/coreos/go-oidc/v3` to resolve the issuer's JWKS URI.
- Signing keys are cached and refreshed with `github.com/lestrrat-go/jwx/v2/jwk.AutoRefresh`.
- JWT validation enforces issuer, audience, expiration (with a small skew allowance), and optional actor client allow-list.
- Responses include the raw claim set to keep the sample transparent for troubleshooting.

## Next steps

- Replace the simple allow-list with database-backed policy decisions.
- Add additional scopes/roles validation before serving business logic.
- Swap the standard library mux with your framework of choice (e.g. chi, echo, gin) while reusing the verification helpers.
