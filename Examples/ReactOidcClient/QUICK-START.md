# Quick Start - React OIDC Client

## ✅ What's Been Fixed

1. **CORS Configuration**: Added proper CORS support for discovery and JWKS endpoints
2. **Local Environment**: Created `.env.local` pointing to local OIDC server
3. **Development Settings**: Updated `appsettings.Development.json` with allowed origins

## 🚀 How to Run

### Prerequisites
- ✅ Node.js 18+ installed (see NODE-UPGRADE-GUIDE.md)
- ✅ .NET 9 SDK installed
- ✅ PostgreSQL running (via Docker/Aspire)

### Step 1: Start the OIDC Server

```powershell
# Terminal 1: From MrWhoOidc root directory
dotnet run --project MrWhoOidc.AppHost
```

Wait for output:
```
✔ Resources started successfully.
  - webauth: https://localhost:7208
```

### Step 2: Start the React App

```powershell
# Terminal 2: From Examples/ReactOidcClient
cd Examples\ReactOidcClient
npm run dev
```

Expected output:
```
  VITE v5.4.3  ready in 500 ms
  
  ➜  Local:   http://localhost:5173/
  ➜  Network: use --host to expose
```

### Step 3: Test the Flow

1. **Open browser**: http://localhost:5173
2. **Click "Login"** button
3. **Accept certificate** warning for https://localhost:7208 (self-signed cert)
4. **Enter credentials**:
   - Username: `admin` (or any seeded user)
   - Password: `Password123!`
5. **Observe redirect** back to http://localhost:5173/callback
6. **View tokens and user info** displayed on the page

## 📋 What Changed

### Server Side (`MrWhoOidc.WebAuth`)

#### 1. CORS Policy Updated
**File**: `Infrastructure/ServiceRegistration/CorsExtensions.cs`

```csharp
// Added GET method for discovery/JWKS
.WithMethods("GET", "POST", "OPTIONS")
```

#### 2. CORS Applied to Discovery Endpoint
**File**: `Infrastructure/EndpointMapping/EndpointMappingExtensions.cs`

```csharp
app.MapGet("/.well-known/openid-configuration", ...)
   .RequireCors("oidc")  // ← Added
   .RequireRateLimiting("rl-authorize");

app.MapGet("/jwks", GetServerJwks)
   .RequireCors("oidc");  // ← Added
```

#### 3. Development Settings Updated
**File**: `appsettings.Development.json`

```json
{
  "Oidc": {
    "Issuer": "https://localhost:7208",
    "AllowedCorsOrigins": [
      "http://localhost:5173",   // React (Vite default)
      "http://localhost:3000",   // React (CRA default)
      "http://localhost:5174"    // Vite alternate
    ]
  }
}
```

### Client Side (`ReactOidcClient`)

#### 1. Local Environment Config
**File**: `.env.local` (created)

```bash
VITE_OIDC_AUTHORITY=https://localhost:7208
VITE_OIDC_CLIENT_ID=react-demo
VITE_REDIRECT_URI=http://localhost:5173/callback
VITE_POST_LOGOUT_REDIRECT_URI=http://localhost:5173/
```

## 🔍 Verify CORS is Working

### Check Discovery Endpoint

**Terminal 3**:
```powershell
# Test CORS preflight (OPTIONS)
curl -X OPTIONS https://localhost:7208/.well-known/openid-configuration `
  -H "Origin: http://localhost:5173" `
  -H "Access-Control-Request-Method: GET" `
  --insecure

# Should return:
# Access-Control-Allow-Origin: http://localhost:5173
# Access-Control-Allow-Methods: GET,POST,OPTIONS
```

**Browser DevTools**:
1. Open http://localhost:5173
2. Open DevTools (F12) → Network tab
3. Click "Login" button
4. Find request to `.well-known/openid-configuration`
5. Check Response Headers for:
   ```
   Access-Control-Allow-Origin: http://localhost:5173
   ```

## 🐛 Troubleshooting

### "CORS policy: No 'Access-Control-Allow-Origin' header"

**Solution**: Restart the OIDC server to load new `appsettings.Development.json`:
```powershell
# Stop MrWhoOidc.AppHost (Ctrl+C)
# Restart:
dotnet run --project MrWhoOidc.AppHost
```

### "net::ERR_CERT_AUTHORITY_INVALID"

**Solution**: This is normal for self-signed certificates.
1. Navigate directly to https://localhost:7208
2. Click "Advanced" → "Proceed to localhost (unsafe)"
3. Return to React app and try login again

### Still connecting to production (mrwho.onrender.com)?

**Solution**: Delete and recreate `.env.local`:
```powershell
cd Examples\ReactOidcClient
Remove-Item .env.local
# Recreate .env.local with local authority
npm run dev  # Restart
```

### "Cannot GET /callback" after redirect

**Solution**: React Router issue. Check:
1. Is React dev server running?
2. Is `src/main.tsx` properly configured with routes?
3. Clear browser cache (Ctrl+Shift+R)

## 📊 Expected Flow Diagram

```
┌─────────────────┐     1. Click Login      ┌──────────────────┐
│  React App      │ ───────────────────────> │  OIDC Server     │
│  localhost:5173 │                          │  localhost:7208  │
└─────────────────┘                          └──────────────────┘
        ↑                                              │
        │                                              │ 2. Show login form
        │                                              │
        │              3. POST credentials             │
        │ <─────────────────────────────────────────── │
        │                                              │
        │              4. Redirect with code           │
        │ <─────────────────────────────────────────── │
        │                                              ↓
        └──────> 5. Exchange code for tokens ──────> [Token endpoint]
                                                       ↓
        ┌────────────────────────────────────────────┐
        │ 6. Display user info and tokens            │
        └────────────────────────────────────────────┘
```

## 🔒 Security Notes

### Development (Current Setup)
- ✅ Localhost origins whitelisted
- ✅ Self-signed certs OK for development
- ✅ No credentials sent via CORS
- ✅ Limited HTTP methods (GET, POST, OPTIONS)

### Production (When Deploying)
- Update `appsettings.json` on server with production React app origin
- Use valid SSL certificates (Let's Encrypt, CloudFlare, etc.)
- Register redirect URIs in client configuration
- Ensure HTTPS for both client and server

## 📚 Related Guides

- **CORS-SETUP-GUIDE.md** - Detailed CORS configuration
- **NODE-UPGRADE-GUIDE.md** - Node.js upgrade instructions
- **README.md** - Project overview

## 🎯 Next Steps

Once working locally:

1. **Register Additional Clients**: Use admin UI at https://localhost:7208/Admin/Clients
2. **Test Different Grant Types**: Authorization Code, Refresh Token, etc.
3. **Add DPoP Support**: See oauth4webapi examples
4. **Deploy to Production**: Follow CORS-SETUP-GUIDE.md for production config

## ✅ Checklist

Before asking for help, verify:

- [ ] Node.js 18+ installed (`node --version`)
- [ ] Both servers running (AppHost + React)
- [ ] `.env.local` exists in ReactOidcClient folder
- [ ] Accepted self-signed cert at https://localhost:7208
- [ ] Browser DevTools Network tab shows CORS headers
- [ ] No other service running on port 5173 or 7208

## 🆘 Still Having Issues?

Check the logs:

**OIDC Server Logs**:
```powershell
# Terminal 1 output shows:
# - Database migrations
# - Seed data creation  
# - CORS policy configuration
# - Request handling
```

**React App Logs**:
```
# Browser DevTools Console (F12)
# - Check for errors
# - Verify OIDC configuration logged
# - See token exchange requests
```

**Common Errors**:
```
❌ "Failed to fetch" → Server not running
❌ "CORS error" → Restart server to load new config
❌ "Invalid client" → Check clientId matches seeded data
❌ "Redirect URI mismatch" → Check .env.local matches server config
```
