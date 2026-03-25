# MrWhoOidc Razor Client Example

This Razor Pages sample shows how to authenticate an interactive web application against `MrWhoOidc.WebAuth` using the `MrWhoOidc.Client` library.

## Prerequisites

- .NET 10 SDK
- A running `MrWhoOidc.WebAuth` instance.
- The seeded `blazor-web` client registration with redirect URI `https://localhost:5003/signin-oidc`.

## Recommended Local Workflows

### Seeded Docker Stack

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

This starts the sample at `https://localhost:5003` and points it at the seeded default tenant:

- issuer: `https://localhost:8443/t/default`
- discovery: `https://localhost:8443/t/default/.well-known/openid-configuration`
- downstream API: `https://localhost:7149`

### Aspire AppHost

```bash
dotnet run --project MrWhoOidc.AppHost
```

This starts the auth server, this Razor client, and `MrWhoOidc.TestApi` together for a .NET-first debugging workflow.

## Running the sample

1. Start the platform locally with either `docker-compose.dev.yml` or `MrWhoOidc.AppHost`.
2. Launch the Razor client:

    ```powershell
    dotnet run --project Examples/MrWhoOidc.RazorClient/MrWhoOidc.RazorClient.csproj
    ```

3. Navigate to `https://localhost:5003` and choose **Sign in with MrWhoOidc**. You should be redirected to the MrWhoOidc login page and, after authenticating, back to the sample where issued tokens and claims are displayed.
4. Visit the **Secure** page to trigger an on-behalf-of exchange. The page uses a typed `HttpClient` with `AddMrWhoOnBehalfOfTokenHandler` to call the sample API (`MrWhoOidc.TestApi`) and renders the returned subject/actor data.

## How it works

- `MrWhoOidc.Client` is registered via `AddMrWhoOidcClient`, exposing the discovery, authorization, and token helper services.
- The `/Auth/Login` page uses `IMrWhoAuthorizationManager` to produce an authorization request with PKCE and caches the verifier in-memory.
- `/Auth/Callback` exchanges the authorization code through `IMrWhoTokenClient`, validates the nonce, and signs the user into a local cookie.
- `/Auth/Logout` lets the user choose between signing out only of this Razor app or federating the sign-out with the issuer using `IMrWhoLogoutManager` from the client package.
- The home page reads cached discovery metadata and displays the stored tokens/claims to prove the flow succeeded.
- The secure page injects `TestApiClient`, which relies on the `IMrWhoOnBehalfOfManager` helper to exchange the signed-in user's access token for one targeted at the downstream API. The resulting access token is attached automatically to the outgoing HTTP request.

In the current multi-tenant development setup, keep `Issuer` tenant-scoped and set `DiscoveryUri` explicitly to the tenant discovery document, for example `https://localhost:8443/t/default/.well-known/openid-configuration`.

If you run against a different tenant or issuer, update the `MrWhoOidc` section in `appsettings.json` accordingly.
