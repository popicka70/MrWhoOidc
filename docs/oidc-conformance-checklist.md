# OpenID Connect Conformance Checklist

**Generated:** 2026-01-05

This checklist maps major OpenID Connect / related RFC normative requirements to the repository implementation and test coverage. Use this as a living appendix for conformance work and auditor review.

| Spec Area | Status | Implemented in (key files) | Tests (Unit/Integration) | Notes / Recommended Action |
|---|---:|---|---|---|
| Discovery (.well-known) | ✅ Implemented | `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` | `MrWhoOidc.UnitTests/Integration/DiscoveryMetadataTests.cs` | Discovery advertises most recommended metadata; ensure `tls_client_certificate_bound_access_tokens` is explicit if CB-TLS is supported. |
| JWKS endpoint | ✅ Implemented | `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/EndpointMappingExtensions.cs` (GetServerJwks), `IKeyStore` implementation | Unit tests for key handling exist; integration tests exercise `/jwks` | Fine. Tenant-aware filtering implemented. |
| Authorization endpoint (code flow) | ✅ Implemented | `MrWhoOidc.WebAuth/Handlers/AuthorizeHandler.cs` | `MrWhoOidc.UnitTests/AuthorizeHandlerTests.cs` | Server currently supports `response_type=code` only. Consider explicit discovery consistency for allowed response types. |
| Response Types (implicit/hybrid) | ✅ Implemented | `RegistrationHandler.cs` (now restricts to `code`), `AuthorizeHandler.cs` (enforces code-only) | `MrWhoOidc.UnitTests/DynamicClientRegistrationTests.cs` (new test `Register_HybridResponseType_Returns400`) | Registration now enforces `response_type=code` for dynamic registrations to avoid RP confusion; consider adding additional response-type flows if needed. |
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
| CIBA (Backchannel, OpenID Connect CIBA) | ⚠️ Partial | `CibaAuthenticationHandler.cs`, `ICibaNotificationService` | Unit tests exist; initial integration tests added (poll/ping) | Initial integration tests added; recommend adding notification delivery/retry and error scenario tests. |
| Dynamic Client Registration (RFC 7591/7592) | ✅ Implemented | `RegistrationHandler.cs`, `IClientConfigurationHandler` endpoints | Unit tests for registration | GET/PUT/DELETE implemented; registration now restricts response_types to `code`. |
| Introspection (RFC 7662) | ✅ Implemented | `IntrospectionHandler.cs` | `Introspection*` tests | OK. |
| Revocation (RFC 7009) | ✅ Implemented | `RevocationHandler.cs` | Unit tests | OK. |
| Token Exchange (RFC 8693) | ✅ Implemented | Token exchange grant handler, `ITokenExchangeService` | Unit tests | OK. |
| DPoP (RFC 9449) | ✅ Implemented | `IDPoPValidator`, `UserInfoHandler`, Token handling | Unit tests for validation and replay; Integration: `MrWhoOidc.UnitTests/TokenHandlerTests.cs::Token_DPoP_Replay_IsRejected_At_TokenEndpoint` | Integration test added to verify replay rejection at `/token`. |
| mTLS (RFC 8705) | ⚠️ Partial | Discovery advertising: `mtls_endpoint_aliases` in `DiscoveryHandler.cs`; mTLS auth checks in Revocation/Token | Unit tests validate self-signed thumbprint logic | Clarify / document whether CB-TLS is used for access tokens and set `tls_client_certificate_bound_access_tokens` if applicable. |
| Session Management (`check_session_iframe`) | ✅ Implemented | `CheckSessionHandler.cs` | Unit tests exist | OK. |
| Logout (Front-channel & Back-channel) | ✅ Implemented | `Logout/*` handlers, Backchannel dispatcher | Unit tests + documentation | OK. |
| Conformance Tests / Integration Coverage | ⚠️ Partial | - | Integration tests exist for many flows; CIBA work in progress and DPoP replay integration test added | Recommendation: add dedicated integration tests in `MrWhoOidc.UnitTests/Integration` for critical flows (CIBA — expand notifications/retries, DPoP nonce tests, PAR/JAR end-to-end, mTLS protected endpoints). |


## How to use this checklist

- Items marked ⚠️ Partial or ❌ Missing should be prioritized for compliance sprints.
- Each `Partial` item includes a recommended action in the Notes column.
- I can open PRs for the high-priority fixes (dynamic registration DELETE, response_type alignment, CIBA integration tests) if you'd like.

---

*End of checklist.*
