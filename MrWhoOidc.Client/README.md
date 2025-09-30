# MrWhoOidc.Client

Client SDK for integrating .NET applications with the MrWhoOidc authorization server. The package offers:

- Strongly-typed options with validation and configuration binding helpers.
- Discovery client with caching and telemetry instrumentation.
- JWKS cache utilities ready for token validation scenarios.
- Token client supporting authorization code, client credentials, refresh tokens, token exchange, and JARM validation.
- Authorization helper capable of emitting JAR request objects (signed with client secret or custom credentials).
- Optional PKCE builder, state/nonce helpers, and DPoP proof generation hooks.

## Getting started

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMrWhoOidcClient(builder.Configuration, sectionName: "MrWhoOidc:Client");

var app = builder.Build();
```

## Configuration highlights

```json
"MrWhoOidc": {
	"Issuer": "https://issuer",
	"ClientId": "web-client",
	"ClientSecret": "super-secret",
	"Scopes": [ "openid", "profile" ],
	"Jar": {
		"Enabled": true,
		"SigningAlgorithm": "HS256",
		"Lifetime": "00:05:00"
	},
	"Jarm": {
		"Enabled": true,
		"ResponseMode": "query.jwt"
	}
}
```

### Troubleshooting JAR/JARM

- `invalid_state` after redirect: ensure the `state` claim inside the JARM payload matches the stored state (cookies can be cleared if callbacks are delayed).
- `invalid_response` with `c_hash` or `s_hash`: verify the authorization server uses the same signing key advertised in discovery/JWKS and that the callback reuses the same `state` and code.
- Missing signing key for request objects: configure either `ClientSecret` (HS256) or provide a custom `Jar.SigningCredentialsResolver` that returns asymmetric credentials with an explicit `kid` for rotation.
- JARM validation failures in integration tests: call `IMrWhoJwksCache.Invalidate()` after rotating server keys so cached metadata refreshes.

See the docs in `/docs/mrwhooidc-client-nuget-backlog.md` for the roadmap and `/docs/developer-guide.md` for server-side integration details.

Additional notes on signed request objects and JARM validation live in `/docs/jar-jarm-guide.md`.
