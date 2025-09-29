# MrWhoOidc.Client

Client SDK for integrating .NET applications with the MrWhoOidc authorization server. The package offers:

- Strongly-typed options with validation and configuration binding helpers.
- Discovery client with caching and telemetry instrumentation.
- JWKS cache utilities ready for token validation scenarios.
- Token client supporting authorization code, client credentials, refresh tokens, and token exchange.
- Optional PKCE builder, state/nonce helpers, and DPoP proof generation hooks.

## Getting started

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMrWhoOidcClient(builder.Configuration, sectionName: "MrWhoOidc:Client");

var app = builder.Build();
```

See the docs in `/docs/mrwhooidc-client-nuget-backlog.md` for the roadmap and `/docs/developer-guide.md` for server-side integration details.
