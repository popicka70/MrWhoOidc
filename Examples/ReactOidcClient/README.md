# React OIDC Demo (MrWhoOidc)

A minimal React + Vite + TypeScript SPA that authenticates against the local MrWho OIDC server using oauth4webapi with PAR and front-channel logout.

Features
- PAR (Pushed Authorization Requests) when advertised by the server
- PKCE (S256)
- Front-channel logout with id_token_hint
- Displays ID Token claims and stored tokens
- TailwindCSS modern styling

Quick start
1. cd Examples/ReactOidcClient
2. npm install
3. npm run dev

Dockerized dev stack
- The repo's dockerized E2E flow serves this app at http://localhost:5173.
- The local auth seed creates a public client with client ID `react-demo` and callback URI `http://localhost:5173/callback`.
- `docker compose -f docker-compose.dev.yml up -d webauth reactclient` builds the SPA with the local authority baked in.

Config
Create `.env` (optional):
```
VITE_OIDC_AUTHORITY=https://localhost:8443/t/default
VITE_OIDC_CLIENT_ID=react-demo
VITE_REDIRECT_URI=http://localhost:5173/callback
VITE_POST_LOGOUT_REDIRECT_URI=http://localhost:5173/
```

Identity Provider
- The local default target is https://localhost:8443/t/default.
- Ensure a public client with `client_id` matching VITE_OIDC_CLIENT_ID exists and allows the configured redirect URI. If the server advertises PAR, the sample will use it automatically.

Notes
- Tokens and claims are stored in sessionStorage for demo purposes only.
- For production, add state/nonce and replay protection storage, and consider silent token refresh.
