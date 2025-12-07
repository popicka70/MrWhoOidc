# MrWhoOidc.OidcDemo

A .NET 10 Razor Pages application demonstrating OpenID Connect (OIDC) authentication using the MrWhoOidc identity provider.

## Overview

This sample application shows how to:

- Configure OIDC authentication with MrWhoOidc IdP
- Redirect users to the identity provider for login
- Display authenticated user information and claims
- Handle logout with RP-initiated logout flow

## Configuration

The application is configured to use the deployed MrWhoOidc IdP at `https://mrwho.onrender.com`.

### Settings (appsettings.json)

| Setting | Value | Description |
|---------|-------|-------------|
| Authority | `https://mrwho.onrender.com/default` | OIDC discovery endpoint (includes tenant) |
| ClientId | `e_LrLUqFsLaTizJh7ERARvfT` | Client identifier |
| ClientSecret | `YOUR_CLIENT_SECRET_HERE` | Client secret (replace with actual value) |
| Scopes | `openid`, `profile`, `email` | Requested OIDC scopes |

### Before Running

1. **Set the Client Secret**: Replace `YOUR_CLIENT_SECRET_HERE` in `appsettings.json` with your actual client secret.

2. **Configure Redirect URI**: Ensure the IdP has `https://localhost:5001/signin-oidc` registered as a valid redirect URI for the client.

3. **Configure Post-Logout Redirect URI**: Ensure `https://localhost:5001/signout-callback-oidc` is registered for logout.

## Running the Application

```bash
cd Examples/MrWhoOidc.OidcDemo
dotnet run
```

Then open `https://localhost:5001` in your browser.

## Authentication Flow

1. User clicks "Sign In with MrWhoOidc"
2. Application redirects to MrWhoOidc IdP (`/default/authorize`)
3. User authenticates at the IdP
4. IdP redirects back to `/signin-oidc` with authorization code
5. Application exchanges code for tokens
6. User is authenticated and claims are displayed

## Project Structure

```
MrWhoOidc.OidcDemo/
├── Pages/
│   ├── Account/
│   │   ├── Login.cshtml(.cs)    # Initiates OIDC login
│   │   └── Logout.cshtml(.cs)   # Handles logout
│   ├── Shared/
│   │   └── _Layout.cshtml       # Site layout
│   ├── Error.cshtml(.cs)        # Error page
│   ├── Index.cshtml(.cs)        # Home page with auth status
│   ├── Secure.cshtml(.cs)       # Protected page (requires auth)
│   ├── _ViewImports.cshtml
│   └── _ViewStart.cshtml
├── Properties/
│   └── launchSettings.json
├── wwwroot/
│   └── css/
│       └── site.css
├── appsettings.json
├── appsettings.Development.json
├── Program.cs
└── MrWhoOidc.OidcDemo.csproj
```

## Technologies

- .NET 10
- ASP.NET Core Razor Pages
- Microsoft.AspNetCore.Authentication.OpenIdConnect
- PKCE (Proof Key for Code Exchange)

## Security Notes

- PKCE is enabled by default for enhanced security
- Cookies are configured with `HttpOnly` and `SecurePolicy.Always`
- HTTPS metadata is required for discovery
