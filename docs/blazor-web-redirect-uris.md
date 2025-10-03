# Blazor Web Client Redirect URIs - Seeding Update

## Overview
Updated the `blazor-web` client seeder configuration to include allowed login and logout redirect URIs for the Blazor application running at `https://localhost:7181/`.

**Updated:** October 2, 2025  
**File Modified:** `MrWhoOidc.Auth/Services/Seeder.cs`

## Changes Made

### Allowed Login Redirect URIs
The following redirect URIs are now seeded for the `blazor-web` client:

```json
[
  "https://localhost:7181/signin-oidc",
  "http://localhost:7181/signin-oidc",
  "https://localhost:5001/signin-oidc",
  "http://localhost:5001/signin-oidc"
]
```

### Allowed Logout Redirect URIs
The following post-logout redirect URIs are seeded:

```json
[
  "https://localhost:7181/signout-callback-oidc",
  "https://localhost:7181/",
  "http://localhost:7181/signout-callback-oidc",
  "http://localhost:7181/",
  "https://localhost:5001/signout-callback-oidc",
  "https://localhost:5001/",
  "http://localhost:5001/signout-callback-oidc",
  "http://localhost:5001/"
]
```

## Seeder Behavior

### New Client Creation
When creating a new `blazor-web` client, the redirect URIs are set immediately along with other configuration.

### Existing Client Backfill
For existing `blazor-web` clients in the database:
- If `AllowedLoginRedirectUrisJson` is empty/null → backfills with the URIs above
- If `AllowedLogoutRedirectUrisJson` is empty/null → backfills with the URIs above
- **Idempotent:** Safe to run multiple times; won't overwrite existing custom URIs

## Client Configuration Summary

The `blazor-web` client is now fully configured with:

| Property | Value |
|----------|-------|
| **ClientId** | `blazor-web` |
| **ClientName** | Blazor Web Frontend |
| **Client Secret** | `z1bvxwNcBXeOP03EMUdawfHnBhx6KAXuYArRSY6a1ZPyme7JMJ_A50bQY75FW6TG` (dev only) |
| **Require PKCE** | ✅ Yes |
| **Require Consent** | ❌ No |
| **Realm** | `admin` |
| **Login Redirect URIs** | Port 7181 and 5001 (HTTP & HTTPS) |
| **Logout Redirect URIs** | Port 7181 and 5001 (HTTP & HTTPS) |
| **Introspection Audiences** | `["api"]` |
| **OBO Enabled** | ✅ Yes |
| **OBO Target Audiences** | `["api"]` |
| **OBO Allowed Scopes** | `["api.read"]` |
| **OBO Max Delegation Depth** | 1 |
| **OBO Max Lifetime** | 15 minutes |

## How to Apply

### Option 1: Restart AppHost (Automatic)
The seeder runs automatically on application startup:

```powershell
# Stop any running instances
# Then start AppHost
dotnet run --project MrWhoOidc.AppHost
```

The seeder will:
1. Check if `blazor-web` client exists
2. If not, create it with redirect URIs
3. If exists, backfill redirect URIs if they're missing

### Option 2: Manual Database Update
If you prefer to update the database manually without restarting:

```sql
UPDATE "Clients"
SET 
    "AllowedLoginRedirectUrisJson" = '["https://localhost:7181/signin-oidc","http://localhost:7181/signin-oidc","https://localhost:5001/signin-oidc","http://localhost:5001/signin-oidc"]',
    "AllowedLogoutRedirectUrisJson" = '["https://localhost:7181/signout-callback-oidc","https://localhost:7181/","http://localhost:7181/signout-callback-oidc","http://localhost:7181/","https://localhost:5001/signout-callback-oidc","https://localhost:5001/","http://localhost:5001/signout-callback-oidc","http://localhost:5001/"]'
WHERE "ClientId" = 'blazor-web';
```

## Blazor App Configuration

### Required appsettings.json
Your Blazor application at `https://localhost:7181/` should have:

```json
{
  "Oidc": {
    "Authority": "https://localhost:5002",
    "ClientId": "blazor-web",
    "ClientSecret": "z1bvxwNcBXeOP03EMUdawfHnBhx6KAXuYArRSY6a1ZPyme7JMJ_A50bQY75FW6TG",
    "ResponseType": "code",
    "Scopes": ["openid", "profile", "email", "offline_access", "roles", "api.read"],
    "UsePkce": true,
    "SaveTokens": true
  }
}
```

### Program.cs Setup
Ensure your Blazor app configures OIDC authentication:

```csharp
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie()
.AddOpenIdConnect(options =>
{
    options.Authority = "https://localhost:5002";
    options.ClientId = "blazor-web";
    options.ClientSecret = "z1bvxwNcBXeOP03EMUdawfHnBhx6KAXuYArRSY6a1ZPyme7JMJ_A50bQY75FW6TG";
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.Scope.Add("offline_access");
    options.Scope.Add("roles");
    options.Scope.Add("api.read");
});
```

## Ports Included

The seeder now supports:

✅ **https://localhost:7181** - Your Blazor app (HTTPS)  
✅ **http://localhost:7181** - Your Blazor app (HTTP fallback)  
✅ **https://localhost:5001** - Alternative Blazor hosting (HTTPS)  
✅ **http://localhost:5001** - Alternative Blazor hosting (HTTP)

## Testing the Configuration

### 1. Start the Services
```powershell
# Terminal 1: Start MrWhoOidc
dotnet run --project MrWhoOidc.AppHost

# Terminal 2: Start your Blazor app
cd <your-blazor-app-path>
dotnet run --urls "https://localhost:7181"
```

### 2. Test Login Flow
1. Navigate to `https://localhost:7181`
2. Click login/sign in
3. Should redirect to `https://localhost:5002/connect/authorize`
4. After authentication, redirects back to `https://localhost:7181/signin-oidc`
5. ✅ Should successfully authenticate

### 3. Test Logout Flow
1. Click logout
2. Should redirect to `https://localhost:5002/connect/endsession`
3. After logout, redirects back to `https://localhost:7181/signout-callback-oidc` or `https://localhost:7181/`
4. ✅ Should successfully sign out

## Verification in pgAdmin

After restarting AppHost with seeder changes:

```sql
-- Check the blazor-web client configuration
SELECT 
    "ClientId",
    "ClientName",
    "AllowedLoginRedirectUrisJson",
    "AllowedLogoutRedirectUrisJson"
FROM "Clients"
WHERE "ClientId" = 'blazor-web';
```

Expected output should show the JSON arrays with all redirect URIs including port 7181.

## Troubleshooting

### "redirect_uri is not allowed"
**Cause:** The redirect URI in your Blazor app doesn't match any seeded URIs  
**Solution:** 
- Check exact URL in browser when error occurs
- Ensure it matches one of the seeded URIs exactly (including trailing slashes)
- Common mistakes: `https://` vs `http://`, port numbers, path casing

### "Invalid client_id"
**Cause:** `blazor-web` client not seeded yet  
**Solution:**
```powershell
# Restart AppHost to trigger seeder
dotnet run --project MrWhoOidc.AppHost
```

### Seeder Not Running
**Cause:** Database already initialized in previous run  
**Solution:** The seeder runs on every startup but only modifies if fields are empty. If you need to force update, either:
1. Clear the specific client's redirect URIs in database, or
2. Delete and recreate the client

### Changes Not Reflected
**Cause:** Running app instances are using cached configuration  
**Solution:**
```powershell
# Stop all running instances
# Restart AppHost
dotnet run --project MrWhoOidc.AppHost
```

## Adding More Redirect URIs

To add additional redirect URIs in the future:

### Option A: Update Seeder (Recommended)
Edit `MrWhoOidc.Auth/Services/Seeder.cs` and add to the arrays:

```csharp
AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { 
    "https://localhost:7181/signin-oidc",
    "https://localhost:7181/signin-oidc",
    "https://localhost:9000/signin-oidc",  // ← Add new URI
    // ... existing URIs
}),
```

### Option B: Direct Database Update
```sql
UPDATE "Clients"
SET "AllowedLoginRedirectUrisJson" = '["https://localhost:7181/signin-oidc","http://localhost:7181/signin-oidc","https://localhost:9000/signin-oidc"]'
WHERE "ClientId" = 'blazor-web';
```

### Option C: Admin API (Future)
Use the admin API endpoints (when implemented) to dynamically manage redirect URIs.

## Security Notes

⚠️ **Development Configuration**  
- The seeded client secret is **for development only**
- HTTP redirect URIs are included for local dev convenience
- `localhost` redirect URIs should **never be used in production**

✅ **Production Recommendations**
- Generate unique client secrets per environment
- Use only HTTPS redirect URIs
- Use actual domain names (not localhost)
- Implement proper consent flow
- Enable stricter PKCE validation

## Related Files

- **Seeder:** `MrWhoOidc.Auth/Services/Seeder.cs`
- **Client Entity:** `MrWhoOidc.Auth/Persistence/Client.cs`
- **Blazor App Config:** `MrWhoOidc.Web/appsettings.json`
- **Authorize Handler:** `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs`

---

**Summary:** The `blazor-web` client now accepts login redirects from `https://localhost:7181/` (and port 5001). Simply restart the AppHost to apply the seeder changes, and your Blazor app should authenticate successfully! ✨
