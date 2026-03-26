# MrWhoOidc.OidcDemo

A .NET 10 Razor Pages application demonstrating OpenID Connect (OIDC) authentication using the MrWhoOidc identity provider.

## Overview

This sample application shows how to:

- Configure OIDC authentication with MrWhoOidc IdP
- Redirect users to the identity provider for login
- Display authenticated user information and claims
- Handle logout with RP-initiated logout flow

## Configuration

The checked-in configuration targets the local development issuer and is also used by `docker-compose.dev.yml`.

### Settings (appsettings.json)

| Setting | Value | Description |
|---------|-------|-------------|
| Authority | `https://localhost:8443/t/default` | Tenant-scoped issuer |
| DiscoveryUri | `https://localhost:8443/t/default/.well-known/openid-configuration` | Discovery document used by the sample |
| ClientId | `blazor-web` | Seeded demo client identifier |
| ClientSecret | configured in `appsettings.json` | Seeded demo client secret |
| Scopes | `openid`, `profile`, `email` | Requested OIDC scopes |

### Before Running

1. Start the local auth server with either `docker-compose.dev.yml` or `MrWhoOidc.AppHost`.
2. If you are using the seeded local stack, no additional client setup is required.
3. If you point the app at a different issuer, make sure that issuer allows `https://localhost:5001/signin-oidc` and `https://localhost:5001/signout-callback-oidc`.

## Running the Application

```bash
cd Examples/MrWhoOidc.OidcDemo
dotnet run
```

Then open `https://localhost:5001` in your browser.

The sample is also included in `docker-compose.dev.yml`.

## Authentication Flow

1. User clicks "Sign In with MrWhoOidc"
2. Application redirects to the tenant-scoped MrWhoOidc issuer
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
