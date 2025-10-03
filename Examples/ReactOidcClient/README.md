# React OIDC Demo (MrWhoOidc)

A minimal React + Vite + TypeScript client that authenticates against the MrWho OIDC server using oauth4webapi with PAR and front-channel logout.

Features
- PAR (Pushed Authorization Requests)
- PKCE (S256)
- Front-channel logout with id_token_hint
- Displays ID Token claims and stored tokens
- TailwindCSS modern styling

Quick start
1. cd Examples/ReactOidcClient
2. npm install
3. npm run dev

Config
Create `.env` (optional):
```
VITE_OIDC_AUTHORITY=https://mrwho.onrender.com
VITE_OIDC_CLIENT_ID=react-demo
VITE_REDIRECT_URI=http://localhost:5173/callback
VITE_POST_LOGOUT_REDIRECT_URI=http://localhost:5173/
```

Identity Provider
- The demo targets https://mrwho.onrender.com by default. Ensure a public client with `client_id` matching VITE_OIDC_CLIENT_ID exists and allows PAR + `redirect_uri`.

Notes
- Tokens and claims are stored in sessionStorage for demo purposes only.
- For production, add state/nonce and replay protection storage, and consider silent token refresh.
