---
title: OIDC Protocol Surface
type: concept
tags: [oidc, oauth, endpoints, protocol]
created: 2026-04-22
updated: 2026-10-05
related_files:
  - MrWhoOidc.WebAuth/Program.cs
  - MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs
  - docs/developer-guide.md
  - docs/oidc-idp-feature-reference.md
  - MrWhoOidc.WebAuth/Handlers/TokenHandler.cs
  - MrWhoOidc.Auth/Services/ClientStore.cs
  - MrWhoOidc.Auth/Services/AuthorizeRequestResolver.cs
  - MrWhoOidc.Auth/Services/Authorization/ResourceIndicatorPolicy.cs
  - MrWhoOidc.Auth/Services/TokenExchangeService.cs
  - MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs
  - MrWhoOidc.WebAuth/Security/ApiBearer/ApiTokenAuthHandler.cs
  - MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiAntiforgeryExtensions.cs
---

MrWhoOidc exposes the standard OIDC and OAuth surfaces through `MrWhoOidc.WebAuth`, while keeping protocol logic and persistence-heavy behavior in `MrWhoOidc.Auth`. The split matters: WebAuth is the HTTP shell, Auth is the behavioral core.

## Main Surfaces

- Discovery and JWKS live in the WebAuth host and are intended to be externally consumable.
- Authorization, token, userinfo, and logout are exposed from the WebAuth surface with handler classes and Razor Pages as needed.
- Admin and tenant management flows share the same host but are conceptually separate from the protocol endpoints.

## Layer Boundary

- `MrWhoOidc.WebAuth` should own routing, endpoint composition, Razor Pages, discovery exposure, and response shaping.
- `MrWhoOidc.Auth` should own protocol validation, persistence, key material, token-related business rules, and durable data access.
- This boundary is reinforced in the repository guidance and should stay stable when new features are added.

## Notable Flows

- Authorization Code with PKCE is a first-class path for browser-based examples.
- Client Credentials and Token Exchange are present for service-to-service and delegated scenarios.
- Client-bound delegated exchange uses an explicit private `delegation_id` parameter. The authenticated confidential client must match the grant's bound client; delegated tokens preserve delegator `sub`, delegate `act.sub`, grant ID, and authorized client.
- DPoP support is part of the repo’s security posture and shows up in both tests and downstream example integrations.
- mTLS-authenticated token-endpoint grants issue sender-constrained access tokens with `cnf.x5t#S256`; JWT and opaque token records retain the certificate binding. Refresh rotation and token exchange require the matching certificate for an already-bound token. UserInfo checks x5t alongside DPoP, and admin API bearer auth accepts mTLS-bound tokens only with the matching request certificate.
- QR login binds desktop polling, completion, and cancellation to the initiating browser's session-specific cookie; mobile confirmation requires number matching. Completion is POST-only, and completion/confirmation/cancellation explicitly validate antiforgery tokens. Desktop polling signals `completionRequired` for platform sign-in so the page submits a protected form; OAuth QR flows retain their RP callback redirect. Mobile Cancel is a local decline, while desktop Cancel invalidates the session. See the [corrected security assessment](../../docs/security-review-2026-10-05.md).

## Client Authentication and Policy

- Credential-less authentication is allowed only for public clients (`token_endpoint_auth_method=none`, or no auth method and no secrets, JWKS, or mTLS thumbprints). This applies at `/token`, `/par`, `/revoke`, `/device/authorize`, and `/bc-authorize`; `/introspect` and CIBA always require a credential.
- `/token` enforces the registered `token_endpoint_auth_method` (or the `AllowClientSecretBasic/Post/PrivateKeyJwt` toggles) and registered `grant_types` (`refresh_token` implied by `authorization_code`). Failures return `invalid_client`, with 401 and `WWW-Authenticate` for Basic.
- PAR `request_uri` values are single-use and bound to the pushing client; `RequirePar` (global, `RequireParClients`, and `Client.RequirePar`) is enforced for every non-PAR request.
- `resource` must be in `Auth:ApiAudiences` or the client's `M2MAllowedAudiencesJson`; at code exchange it must match the resource bound to the code.
- Introspection is deny-by-default: own tokens, tokens whose `aud` names the caller, or audiences granted via `IntrospectionAudiencesJson` / `Auth:IntrospectionPermissions`.
- Device authorization checks `AllowDeviceAuthorization`; CIBA checks `AllowCiba`, resolves the hint to a user (`unknown_user_id` otherwise), and only that user may approve.
- Encrypted ID tokens and JARM fail closed when the client's encryption key or algorithm is unavailable.
- `X-Client-Cert` (mTLS behind a proxy) is honoured only from loopback or configured `ForwardedHeaders` known proxies/networks.
- Client-secret create, activate, set-primary, and revoke admin API writes require antiforgery validation for cookie-authenticated callers; successfully authenticated `api-bearer` clients remain supported. Razor Page secret forms use MVC antiforgery validation.

## Related Pages

- [[mrwhooidc-auth]]
- [[mrwhooidc-webauth]]
- [[backchannel-logout]]
- [[testing-strategy]]