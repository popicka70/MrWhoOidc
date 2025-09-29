# MrWhoOidc Razor Client Example

This Razor Pages sample shows how to authenticate an interactive web application against `MrWhoOidc.WebAuth` using the `MrWhoOidc.Client` library.

## Prerequisites

- .NET 9 SDK (preview)
- A running instance of `MrWhoOidc.WebAuth` exposed at `https://localhost:7208`. The Aspire host (`MrWhoOidc.AppHost`) starts one for local development.
- The `mrwho-admin` client registration seeded by `MrWhoOidc.Auth` with redirect URI `https://localhost:5003/signin-oidc`.

## Running the sample

1. Start the platform locally (for example via `dotnet run --project MrWhoOidc.AppHost`).
2. Launch the Razor client:

    ```powershell
    dotnet run --project Examples/MrWhoOidc.RazorClient/MrWhoOidc.RazorClient.csproj
    ```

3. Navigate to `https://localhost:5003` and choose **Sign in with MrWhoOidc**. You should be redirected to the MrWhoOidc login page and, after authenticating, back to the sample where issued tokens and claims are displayed.

## How it works

- `MrWhoOidc.Client` is registered via `AddMrWhoOidcClient`, exposing the discovery, authorization, and token helper services.
- The `/Auth/Login` page uses `IMrWhoAuthorizationManager` to produce an authorization request with PKCE and caches the verifier in-memory.
- `/Auth/Callback` exchanges the authorization code through `IMrWhoTokenClient`, validates the nonce, and signs the user into a local cookie.
- The home page reads cached discovery metadata and displays the stored tokens/claims to prove the flow succeeded.

Adjust the configuration in `appsettings.json` if you register a different client or change the issuer URL.
