# CORS Configuration Guide - ReactOidcClient

## Problem

When connecting the React client (`localhost:5173`) to the OIDC server, you get:

```
Access to fetch at 'https://mrwho.onrender.com/.well-known/openid-configuration' 
from origin 'http://localhost:5173' has been blocked by CORS policy: 
No 'Access-Control-Allow-Origin' header is present on the requested resource.
```

## Solution Options

### ✅ Option 1: Use Local OIDC Server (Recommended for Development)

**Step 1**: I've already created `.env.local` for you with local server configuration:

```bash
VITE_OIDC_AUTHORITY=https://localhost:8443/t/default
VITE_OIDC_CLIENT_ID=react-demo
VITE_REDIRECT_URI=http://localhost:5173/callback
VITE_POST_LOGOUT_REDIRECT_URI=http://localhost:5173/
```

**Step 2**: Ensure the local development stack is running:

```powershell
# In MrWhoOidc root directory
docker compose -f docker-compose.dev.yml up -d --build webauth reactclient postgres redis
```

**Step 3**: Restart your React dev server:

```powershell
# In Examples/ReactOidcClient
npm run dev
```

**Step 4**: Navigate to http://localhost:5173 and test login.

✅ **This will work immediately** because:
- The local dev stack already exposes the authority and seed data expected by the React sample
- The server can be configured to allow the React origin for discovery and token flows
- Faster development cycle

---

### Option 2: Configure CORS on Production Server

If you need to connect to `mrwho.onrender.com`, you must add CORS configuration.

#### On the Server (MrWhoOidc.WebAuth)

**Step 1**: Update `appsettings.json` to include CORS origins:

```json
{
  "Oidc": {
    "Issuer": "https://mrwho.onrender.com",
    "AllowedCorsOrigins": [
      "http://localhost:5173",
      "https://your-react-app.vercel.app"
    ]
  }
}
```

**Step 2**: For local development, update `appsettings.Development.json`:

```json
{
  "Oidc": {
    "Issuer": "https://localhost:8443",
    "AllowedCorsOrigins": [
      "http://localhost:5173",
      "http://localhost:3000",
      "http://localhost:5174"
    ]
  }
}
```

**Step 3**: Restart the OIDC server for changes to take effect.

#### CORS Policy Details

The current CORS policy (`CorsExtensions.cs`) allows:
- **Origins**: Configurable via `AllowedCorsOrigins`
- **Methods**: `POST`, `OPTIONS`
- **Headers**: `authorization`, `content-type`
- **Credentials**: Disallowed (no cookies/auth headers)

This policy applies to OIDC endpoints (token, userinfo, etc.).

---

## Configuration Files Priority

Vite loads environment files in this order (later overrides earlier):

1. `.env` - Base configuration (checked into git)
2. `.env.local` - Local overrides (gitignored) ✅ **Use this!**
3. `.env.development` - Development-specific
4. `.env.development.local` - Local dev overrides

**`.env.local` is already in `.gitignore`**, so your local config won't be committed.

---

## Current Setup

### Local Development (Recommended):
```
React App         OIDC Server
localhost:5173 ←→ localhost:8443
    ↓
[CORS still applies because the ports differ]
```

### Production:
```
React App              OIDC Server
localhost:5173 ←→ mrwho.onrender.com
    ↓
[CORS required - different domains]
```

---

## Quick Start (Local Development)

```powershell
# Terminal 1: Start OIDC Server
docker compose -f docker-compose.dev.yml up -d --build webauth reactclient postgres redis

# Terminal 2: Start React App (after Node.js upgrade)
cd Examples\ReactOidcClient
npm run dev

# Browser: Navigate to
http://localhost:5173
```

Expected flow:
1. Click "Login" → Redirects to https://localhost:8443/t/default/authorize
2. Enter credentials → Login
3. Redirects back to http://localhost:5173/callback
4. App shows user info and tokens

---

## Troubleshooting

### Still getting CORS errors with local server?

**Issue**: Browser might be caching old CORS policies.

**Fix**:
```
1. Open DevTools (F12)
2. Go to Network tab
3. Check "Disable cache"
4. Hard refresh (Ctrl+Shift+R)
```

### Certificate warnings with localhost:8443?

**Issue**: Self-signed certificate for local HTTPS.

**Fix**: Accept the certificate warning in browser:
1. Navigate to https://localhost:8443
2. Click "Advanced"
3. Click "Proceed to localhost (unsafe)"

### React app still connecting to production?

**Issue**: `.env.local` not being loaded.

**Fix**:
```powershell
# Stop the React dev server (Ctrl+C)
# Delete .env.local and recreate it
# Restart:
npm run dev
```

### Need to test against production?

**On Production Server**: Add CORS configuration as shown in Option 2.

**Local `.env.local`**:
```bash
VITE_OIDC_AUTHORITY=https://mrwho.onrender.com
VITE_OIDC_CLIENT_ID=react-demo
VITE_REDIRECT_URI=http://localhost:5173/callback
VITE_POST_LOGOUT_REDIRECT_URI=http://localhost:5173/
```

Then ensure the server has `AllowedCorsOrigins` configured.

---

## Security Notes

### Development
- Localhost CORS is safe for development
- Self-signed certs are normal for local HTTPS

### Production
- Only whitelist specific origins (never use `*`)
- Use HTTPS for both client and server
- Ensure redirect URIs are registered in client configuration

Current CORS policy is **secure** because:
✅ No credentials allowed (DisallowCredentials)  
✅ Specific methods only (POST, OPTIONS)  
✅ Limited headers (authorization, content-type)  
✅ Must explicitly whitelist origins (no wildcards)

---

## Next Steps

1. ✅ **Node.js upgraded** (see NODE-UPGRADE-GUIDE.md)
2. ✅ **`.env.local` created** (points to local server)
3. ⏳ **Start both servers** (AppHost + React)
4. ⏳ **Test OIDC flow** (login/logout)
5. 🔄 **Add CORS to production** (when deploying React app)

---

## Production Deployment Checklist

When deploying the React app:

### React App
- [ ] Update `.env.production` with production OIDC authority
- [ ] Set correct redirect URIs for production domain
- [ ] Build: `npm run build`
- [ ] Deploy `dist` folder to hosting (Vercel, Netlify, etc.)

### OIDC Server
- [ ] Add production React app origin to `AllowedCorsOrigins`
- [ ] Register the client with correct redirect URIs
- [ ] Update client configuration in database/seed
- [ ] Restart server to apply CORS changes

---

## References

- CORS Policy: `MrWhoOidc.WebAuth/Infrastructure/ServiceRegistration/CorsExtensions.cs`
- OIDC Config: `Examples/ReactOidcClient/src/oidc/config.ts`
- Server Settings: `MrWhoOidc.WebAuth/appsettings.json`
