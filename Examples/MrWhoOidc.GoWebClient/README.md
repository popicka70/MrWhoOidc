# MrWhoOidc.GoWebClient

Go HTTP sample that completes the authorization-code + PKCE flow against your local `MrWhoOidc.WebAuth` issuer, shows the resulting tokens, and (optionally) exchanges them on-behalf-of a downstream API.

## Prerequisites

- Go 1.22 or newer
- A running `MrWhoOidc.WebAuth` instance (the Aspire host `MrWhoOidc.AppHost` starts one on `https://localhost:7208`)
- A public client registration (e.g. `mrwho-go-web`) with redirect URI `http://localhost:5080/callback`
- Optional: a confidential client allowed to perform OBO token exchange (e.g. `mrwho-go-obo`) with access to the sample API audience (`api`)

> Tip: Trust the .NET development certificate (`dotnet dev-certs https --trust`) so the Go HTTP client accepts the issuer's TLS certificate.

## Setup

1. Copy `config.example.json` to `config.json` (or point `MRWHO_GO_WEB_CONFIG` to a custom path).
2. Adjust the configuration:
   - `issuer`: base URL of your running MrWhoOidc server.
   - `client_id` / `client_secret`: the interactive client credentials. Leave `client_secret` blank for public PKCE clients.
   - `redirect_url`: must match the redirect registered with the issuer.
   - `api_base_url`: base address of the downstream API (e.g. `https://localhost:7149`).
   - `obo`: optional section for on-behalf-of token exchange. Remove it to use only the interactive access token.
3. Ensure the chosen clients exist. You can create them via the admin UI or seed data; add the redirect URI and grant the `api.read` scope when using the sample API.

## Run it

```powershell
cd Examples/MrWhoOidc.GoWebClient
go run .
```

Then navigate to `http://localhost:5080/` and choose **Sign in**. After authenticating you will see the issued tokens, ID token claims, and cached UserInfo payload. Use **Invoke GET /me** to call the sample API; when the `obo` section is configured the app first performs a token exchange so the API receives an audience-specific access token.

Environment variables:
- `MRWHO_GO_WEB_CONFIG`: absolute or relative path to the JSON configuration file.

## How it works

- Discovery metadata and JWKS are obtained with [`github.com/coreos/go-oidc/v3`](https://pkg.go.dev/github.com/coreos/go-oidc/v3/oidc).
- Authorization requests are generated with [`golang.org/x/oauth2`](https://pkg.go.dev/golang.org/x/oauth2), using PKCE by default.
- ID tokens are validated locally; UserInfo responses are cached per session.
- On-behalf-of (token exchange) requests POST to the issuer's token endpoint with `grant_type=urn:ietf:params:oauth:grant-type:token-exchange` and reuse the logged-in user's access token as the `subject_token`.

## Next steps

- Wire the sample into your automation by swapping the HTML view for your preferred templating or frontend.
- Extend the token caching logic to persist refresh tokens across restarts.
- Switch to HTTPS locally (for example via `mkcert`) so cookies can be marked `Secure`.
