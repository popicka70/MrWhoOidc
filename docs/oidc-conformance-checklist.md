# OpenID Connect Conformance Checklist

**Generated:** 2026-01-05

This checklist maps major OpenID Connect / related RFC normative requirements to the repository implementation and test coverage. Use this as a living appendix for conformance work and auditor review.

| Spec Area | Status | Implemented in (key files) | Tests (Unit/Integration) | Notes / Recommended Action |
|---|---:|---|---|---|
| Discovery (.well-known) | ✅ Implemented | `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` | `MrWhoOidc.UnitTests/Integration/DiscoveryMetadataTests.cs` | Discovery advertises most recommended metadata; ensure `tls_client_certificate_bound_access_tokens` is explicit if CB-TLS is supported. |
| JWKS endpoint | ✅ Implemented | `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` (GetServerJwks), `IKeyStore` implementation | Unit tests for key handling exist; integration tests exercise `/jwks` | Fine. Tenant-aware filtering implemented. |
| Authorization endpoint (code flow) | ✅ Implemented | `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs` | `MrWhoOidc.UnitTests/AuthorizeHandlerTests.cs` | Server currently supports `response_type=code` only. Consider explicit discovery consistency for allowed response types. |
| Response Types (implicit/hybrid) | ⚠️ Partial | `RegistrationHandler.cs` (accepts hybrids), `AuthorizeHandler.cs` (enforces code-only) | Unit tests detect unsupported response types | Recommendation: align dynamic registration acceptance or implement additional response-type flows/validation in `AuthorizeHandler`. |
| Token endpoint (grant types incl. refresh, client_credentials, device_code, token_exchange) | ✅ Implemented | `MrWhoOidc.WebAuth/Handlers/TokenHandler.cs` and grant handlers | `MrWhoOidc.UnitTests/*Token*` | DPoP, token exchange and rate limits implemented. |
| PKCE (S256) | ✅ Implemented | `AuthorizeRequestValidator`, token exchangers | Unit tests exist | OK. |
| ID Token (signing: at_hash/c_hash support) | ✅ Implemented | `MrWhoOidc.Auth/Services/JwtService.cs`, `AuthorizationCodeExchanger.cs` | Unit tests for at_hash/c_hash | OK. Support for multiple algs is tenant-driven. |
| ID Token Encryption (JWE) | ⚠️ Partial | `JwtService.CreateJwtEncryptedAsync` used in some flows | Unit tests for nested JWE exist | Client encryption metadata supported; ensure discovery and registration validation fully aligned. |
| UserInfo endpoint (signed/encrypted responses, claims handling) | ✅ Implemented | `MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs` | `UserInfoHandlerTests.cs` | JWT/JWE responses supported; claims parameter and constraints enforced. |
| Claims request parameter | ✅ Implemented | `AuthorizeRequest` handling, persisted metadata, `UserInfoHandler` filtering | Unit tests validate claims filtering | OK. |
| PAR (Pushed Authorization Requests) | ✅ Implemented | `MrWhoOidc.WebAuth/Handlers/ParHandler.cs` | `ParHandlerTests.cs` | OK. Enforces auth and request object validation. |
| JAR (request objects) | ✅ Implemented | `IRequestObjectValidator` usages in PAR and authorize | Unit tests for request object validation | OK. |
| JARM (JWT Authorization Response Mode) | ✅ Implemented | `IJarmService`, `AuthorizeResponseGenerator.cs` | `JarmServiceTests.cs` | OK. |
| Device Authorization Grant (RFC 8628) | ✅ Implemented | `DeviceAuthorizationHandler.cs` | Unit tests present | OK. |
| CIBA (Backchannel, OpenID Connect CIBA) | ⚠️ Partial | `CibaAuthenticationHandler.cs`, `ICibaNotificationService` | Unit tests exist; missing integration E2E tests | Recommendation: add full integration tests for poll/ping and notification flows. |
| Dynamic Client Registration (RFC 7591/7592) | ⚠️ Partial | `RegistrationHandler.cs`, `IClientConfigurationHandler` endpoints | Unit tests for registration | Missing `DELETE /register/{clientId}` (RFC 7592). Add endpoint + tests. |
| Introspection (RFC 7662) | ✅ Implemented | `IntrospectionHandler.cs` | `Introspection*` tests | OK. |
| Revocation (RFC 7009) | ✅ Implemented | `RevocationHandler.cs` | Unit tests | OK. |
| Token Exchange (RFC 8693) | ✅ Implemented | Token exchange grant handler, `ITokenExchangeService` | Unit tests | OK. |
| DPoP (RFC 9449) | ✅ Implemented | `IDPoPValidator`, `UserInfoHandler`, Token handling | Unit tests for validation and replay | OK — ensure replay/nonce integration tests present. |
| mTLS (RFC 8705) | ⚠️ Partial | Discovery advertising: `mtls_endpoint_aliases` in `DiscoveryHandler.cs`; mTLS auth checks in Revocation/Token | Unit tests validate self-signed thumbprint logic | Clarify / document whether CB-TLS is used for access tokens and set `tls_client_certificate_bound_access_tokens` if applicable. |
| Session Management (`check_session_iframe`) | ✅ Implemented | `CheckSessionHandler.cs` | Unit tests exist | OK. |
| Logout (Front-channel & Back-channel) | ✅ Implemented | `Logout/*` handlers, Backchannel dispatcher | Unit tests + documentation | OK. |
| Conformance Tests / Integration Coverage | ⚠️ Partial | - | Integration tests exist for many flows; CIBA and some mTLS end-to-end tests missing | Recommendation: add dedicated integration tests in `MrWhoOidc.UnitTests/Integration` for critical flows (CIBA, DPoP replay+nonce, PAR/JAR end-to-end, mTLS protected endpoints).


## How to use this checklist

- Items marked ⚠️ Partial or ❌ Missing should be prioritized for compliance sprints.
- Each `Partial` item includes a recommended action in the Notes column.
- I can open PRs for the high-priority fixes (dynamic registration DELETE, response_type alignment, CIBA integration tests) if you'd like.

---

*End of checklist.*
