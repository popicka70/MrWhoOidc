# OIDC Identity Provider Feature Reference

**Date:** 2026-03-10  
**Purpose:** Provide a spec-grounded feature list for an OpenID Connect Identity Provider (OpenID Provider / Authorization Server), based on OpenID Connect specifications and related OAuth RFCs.

This document separates features into three groups:

- **Baseline**: features a modern OIDC IdP is expected to have for broad interoperability.
- **Strongly recommended**: features that materially improve security, interoperability, or deployability.
- **Optional / advanced**: features needed only for specific client types, assurance levels, or ecosystems.

It also calls out older features that are defined by older specifications but are no longer recommended for new deployments.

## 1. Baseline Features

These are the features an OIDC IdP should normally implement before calling itself production-ready.

| Feature | Why it matters | Primary references |
|---|---|---|
| HTTPS everywhere, stable issuer identifier, strict TLS validation | OIDC and OAuth rely on a trustworthy issuer and protected front-channel/back-channel communication. | [OIDC Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html), [RFC 8414](https://www.rfc-editor.org/rfc/rfc8414), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| Discovery document at `/.well-known/openid-configuration` | Clients need a standard way to find endpoints, supported scopes, algorithms, and capabilities. | [OIDC Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html), [RFC 8414](https://www.rfc-editor.org/rfc/rfc8414) |
| Authorization endpoint | Required for browser-based authentication requests. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 6749](https://www.rfc-editor.org/rfc/rfc6749) |
| Token endpoint | Required to exchange authorization codes for tokens and to support client authentication. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 6749](https://www.rfc-editor.org/rfc/rfc6749) |
| JWKS endpoint | Relying Parties need public keys to validate ID Tokens and other signed artifacts. | [OIDC Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html), [RFC 7517](https://www.rfc-editor.org/rfc/rfc7517) |
| Authorization Code Flow | This is the modern primary interactive login flow for web, SPA, and native clients. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| ID Token issuance and validation semantics | The IdP must issue standards-compliant ID Tokens with correct claims and signing behavior. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 7519](https://www.rfc-editor.org/rfc/rfc7519), [RFC 7515](https://www.rfc-editor.org/rfc/rfc7515) |
| Support for core claims and scopes | Clients expect at least `openid` and commonly `profile`, `email`, `address`, and `phone`, plus the standard claim model behind them. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html) |
| UserInfo endpoint | Needed when the RP obtains user claims separately from the ID Token. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html) |
| Registered-client model with redirect URI validation | Every RP needs controlled metadata such as redirect URIs, grant types, response types, and auth methods. Exact redirect URI validation is a baseline security control. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 6749](https://www.rfc-editor.org/rfc/rfc6749), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| Client authentication at the token endpoint | Confidential clients need a standard authentication method such as `client_secret_basic`, `client_secret_post`, `private_key_jwt`, or `tls_client_auth`. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 8705](https://www.rfc-editor.org/rfc/rfc8705) |
| `state` and `nonce` handling | These remain core defenses for correlation, CSRF protection, and replay protection in front-channel flows. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| Consent and session handling | OIDC authentication is not only token issuance; the OP must authenticate the end-user, track session state, and apply consent correctly. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [OIDC Session Management 1.0](https://openid.net/specs/openid-connect-session-1_0.html) |
| Standards-compliant error responses | Clients depend on interoperable OAuth and OIDC errors at authorization, token, and userinfo endpoints. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 6749](https://www.rfc-editor.org/rfc/rfc6749), [RFC 6750](https://www.rfc-editor.org/rfc/rfc6750) |
| Key lifecycle management and `kid` publication | A production IdP should publish stable signing keys, rotate them safely, and keep metadata truthful during rollover. | [OIDC Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html), [RFC 7517](https://www.rfc-editor.org/rfc/rfc7517), [RFC 7515](https://www.rfc-editor.org/rfc/rfc7515) |

## 2. Strongly Recommended Features

These features are not all part of the OIDC core minimum, but a serious internet-facing IdP should usually support most of them.

| Feature | Why it matters | Primary references |
|---|---|---|
| PKCE for public clients, and preferably all authorization code clients | PKCE is now standard hardening, not just a mobile-app add-on. | [RFC 7636](https://www.rfc-editor.org/rfc/rfc7636), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| OAuth Security BCP posture | New deployments should avoid legacy insecure patterns, especially the implicit grant and weak redirect validation practices. | [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| RP-initiated logout endpoint | Most RPs expect a standard logout round-trip to the OP. | [OIDC RP-Initiated Logout 1.0](https://openid.net/specs/openid-connect-rpinitiated-1_0.html) |
| Front-channel and back-channel logout support | Multi-application sign-out is a common enterprise requirement; back-channel logout is especially useful for reliability. | [OIDC Front-Channel Logout 1.0](https://openid.net/specs/openid-connect-frontchannel-1_0.html), [OIDC Back-Channel Logout 1.0](https://openid.net/specs/openid-connect-backchannel-1_0.html) |
| Session management support | Browser session awareness improves SSO/logout interoperability for web clients. | [OIDC Session Management 1.0](https://openid.net/specs/openid-connect-session-1_0.html) |
| Pairwise subject identifiers | Pairwise subjects reduce cross-client correlation and are important for privacy-sensitive deployments. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html) |
| `prompt`, `max_age`, `auth_time`, `acr`, and `amr` support | These are core interoperability features for login UX, step-up auth, and assurance signaling. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html) |
| `claims` parameter support | Lets clients request precise ID Token and UserInfo claims instead of relying only on coarse scopes. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html) |
| Signed and encrypted ID Tokens and UserInfo responses | Important for higher-assurance integrations and intermediated architectures. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 7516](https://www.rfc-editor.org/rfc/rfc7516), [RFC 7518](https://www.rfc-editor.org/rfc/rfc7518) |
| Token revocation endpoint | RPs and confidential clients often need a standard way to invalidate refresh tokens or reference tokens. | [RFC 7009](https://www.rfc-editor.org/rfc/rfc7009) |
| Token introspection endpoint | Useful when access tokens are opaque or when resource servers need central token status checks. | [RFC 7662](https://www.rfc-editor.org/rfc/rfc7662) |
| Authorization server issuer identification in authorization responses | Helps mitigate mix-up style attacks in multi-issuer client deployments. | [RFC 9207](https://www.rfc-editor.org/rfc/rfc9207), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| Sender-constrained token support through DPoP or mTLS | Reduces token replay risk, especially for APIs and native/public clients. | [RFC 9449](https://www.rfc-editor.org/rfc/rfc9449), [RFC 8705](https://www.rfc-editor.org/rfc/rfc8705) |
| Pushed Authorization Requests (PAR) | Moves sensitive authorization parameters off the browser and improves request integrity. | [RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) |
| JWT Secured Authorization Request (JAR) / request object support | Lets clients sign and optionally encrypt authorization requests. | [RFC 9101](https://www.rfc-editor.org/rfc/rfc9101), [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html) |
| Native app interoperability practices | If the IdP serves mobile/desktop apps, it should handle native-app redirect models and security requirements correctly. | [RFC 8252](https://www.rfc-editor.org/rfc/rfc8252), [RFC 7636](https://www.rfc-editor.org/rfc/rfc7636) |
| Dynamic client registration and registration management | Important for SaaS ecosystems, federation brokers, and automated onboarding. | [OIDC Dynamic Client Registration 1.0](https://openid.net/specs/openid-connect-registration-1_0.html), [RFC 7591](https://www.rfc-editor.org/rfc/rfc7591), [RFC 7592](https://www.rfc-editor.org/rfc/rfc7592) |
| Client Credentials Grant | Required for machine-to-machine / service-to-service scenarios where no end-user is involved. A core OAuth 2.0 grant type. | [RFC 6749 §4.4](https://www.rfc-editor.org/rfc/rfc6749#section-4.4) |
| Refresh token rotation and reuse detection | Rotating refresh tokens on every use and detecting stolen-token replay are explicit RFC 9700 recommendations for limiting exposure when tokens leak. This includes token family tracking and cascade revocation on reuse. | [RFC 9700 §4.14](https://www.rfc-editor.org/rfc/rfc9700#section-4.14), [RFC 6749](https://www.rfc-editor.org/rfc/rfc6749) |
| Response mode support (`query`, `fragment`, `form_post`) | Clients may need different delivery mechanisms for authorization responses. `form_post` avoids URL length limits and referrer leakage and is common for server-side RPs. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [OAuth 2.0 Form Post Response Mode](https://openid.net/specs/oauth-v2-form-post-response-mode-1_0.html) |
| `offline_access` scope and refresh token issuance control | Standardized mechanism for clients to explicitly request refresh tokens. The OP should require this scope (or equivalent policy) rather than issuing refresh tokens unconditionally. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| JWT Profile for OAuth 2.0 Access Tokens | Standardizes JWT access token structure (header, claims, validation rules) so that resource servers can validate tokens locally without calling introspection. Improves interoperability across API ecosystems. | [RFC 9068](https://www.rfc-editor.org/rfc/rfc9068) |
| `login_hint`, `id_token_hint`, `display`, and `ui_locales` parameter support | These OIDC Core parameters let RPs influence the authentication UX: pre-select the user, skip login if already authenticated, request specific display formats, and support localization. | [OIDC Core 1.0 §3.1.2.1](https://openid.net/specs/openid-connect-core-1_0.html#AuthRequest) |

## 3. Optional or Advanced Features

These features are worth supporting when your client ecosystem or assurance requirements call for them.

| Feature | When to add it | Primary references |
|---|---|---|
| Device Authorization Grant | Useful for TVs, CLIs, and devices without a normal browser UX. | [RFC 8628](https://www.rfc-editor.org/rfc/rfc8628) |
| Token Exchange | Needed when the OP also participates in delegation or service-to-service identity translation scenarios. | [RFC 8693](https://www.rfc-editor.org/rfc/rfc8693) |
| Resource Indicators | Useful when clients need explicit audience/resource targeting across multiple APIs. | [RFC 8707](https://www.rfc-editor.org/rfc/rfc8707) |
| JWT Secured Authorization Response Mode (JARM) | Helpful for higher-assurance clients that want signed authorization responses. | [JARM](https://openid.net/specs/oauth-v2-jarm.html) |
| Client Initiated Backchannel Authentication (CIBA) | Useful for decoupled auth flows where the user authenticates on another device. | [OpenID Connect CIBA Core 1.0](https://openid.net/specs/openid-client-initiated-backchannel-authentication-core-1_0.html) |
| Rich Authorization Requests (RAR) | Appropriate when the authorization request needs structured authorization detail rather than simple scopes. | [RFC 9396](https://www.rfc-editor.org/rfc/rfc9396) |
| OpenID Federation | Important when trust is established dynamically across organizations and metadata chains. | [OpenID Federation 1.0](https://openid.net/specs/openid-federation-1_0.html) |
| Identity assurance / verified claims | Add this when the IdP is expected to deliver verified identity evidence, not just authentication. | [OpenID Connect for Identity Assurance 1.0](https://openid.net/specs/openid-connect-4-identity-assurance-1_0.html) |
| Shared Signals / session risk events | Useful when the IdP participates in cross-system risk and session state signaling. | [OpenID Shared Signals Framework 1.0](https://openid.net/specs/openid-sharedsignals-framework-1_0.html) |
| FAPI security profiles | Add these when targeting banking, regulated APIs, or high-assurance ecosystems. | [FAPI 2.0 Security Profile](https://openid.net/specs/fapi-security-profile-2_0.html), [FAPI 1.0 Advanced](https://openid.net/specs/openid-financial-api-part-2-1_0.html) |
| Step-Up Authentication Challenge Protocol | When APIs need to dynamically demand a higher authentication level mid-session (e.g., payment confirmation triggers MFA re-auth). Uses `insufficient_user_authentication` error and `acr_values` / `max_age` in the challenge. | [RFC 9470](https://www.rfc-editor.org/rfc/rfc9470) |
| Aggregated and Distributed Claims | When user claims originate from external sources (e.g., a credential issuer or verifiable data registry). The OP returns claim references or signed claim sets from third parties rather than flat claim values. | [OIDC Core 1.0 §5.6](https://openid.net/specs/openid-connect-core-1_0.html#AggregatedDistributedClaims) |
| Self-Issued OpenID Provider v2 (SIOPv2) | When supporting decentralized / wallet-based identity flows where the user's own device acts as the OP and presents verifiable credentials. | [OpenID Connect Self-Issued OpenID Provider v2](https://openid.net/specs/openid-connect-self-issued-v2-1_0.html) |
| JWT Bearer Assertion Grant | When external identity systems need to exchange signed JWT assertions for local access tokens without a browser-based authorization flow. Useful for service identity bootstrapping. | [RFC 7523](https://www.rfc-editor.org/rfc/rfc7523) |
| OAuth 2.0 Protected Resource Metadata | When resource servers need to advertise their token requirements (accepted issuers, required scopes, supported auth methods) in a machine-readable discovery document. | [RFC 9728](https://www.rfc-editor.org/rfc/rfc9728) |
| OAuth 2.0 for Browser-Based Applications guidance | When serving SPAs and browser-based clients. Goes beyond the general Security BCP with specific recommendations for token storage, BFF patterns, and browser-side token handling. | [draft-ietf-oauth-browser-based-apps](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-browser-based-apps) |
| Global / Session-Wide Token Revocation | When revoking a single token is not enough and the OP must cascade revocation across all tokens issued within a session, for a user, or for a client. Essential for enterprise single-logout completeness. | [RFC 7009](https://www.rfc-editor.org/rfc/rfc7009), [OIDC Back-Channel Logout 1.0](https://openid.net/specs/openid-connect-backchannel-1_0.html) |
| Cross-Device Authentication (QR-based) | When users initiate authentication on one device (e.g., desktop) and confirm it on another (e.g., phone via QR code scan). Complements CIBA for consumer-facing scenarios. | Industry pattern; related to [FIDO2/WebAuthn](https://www.w3.org/TR/webauthn-3/) |
| External Identity Provider Chaining (Identity Brokering) | When the OP federates authentication to upstream IdPs (social login, enterprise SAML/OIDC) and maps external claims to local identity. Includes account linking, JIT provisioning, and claim transformation. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), SAML 2.0 |
| Multi-Tenancy | When a single IdP deployment serves multiple isolated organizations, each with separate client registrations, users, signing keys, branding, and policies. Critical for SaaS IdP offerings. | Deployment pattern |
| WebAuthn / Passkeys as OP authentication factors | When the OP itself offers passwordless or phishing-resistant authentication using platform authenticators or roaming security keys. Directly improves the security posture of every RP that delegates authentication. | [W3C Web Authentication Level 3](https://www.w3.org/TR/webauthn-3/), [FIDO2](https://fidoalliance.org/fido2/) |
| Incremental / Granular Consent | When clients expand their requested scopes over time and the OP must track, merge, and present consent grants incrementally rather than re-prompting for previously granted permissions. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html) (implementation guidance) |

## 4. Features Defined by Older Specs but Not Recommended for New Deployments

These items may still appear in older clients or legacy compatibility matrices, but they should not be the default direction for a new IdP.

| Feature | Current guidance | References |
|---|---|---|
| Implicit Flow | Defined by OIDC Core, but modern deployments should prefer Authorization Code Flow with PKCE. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| Hybrid Flow as a default | Still valid for some cases, but usually unnecessary unless a client has a concrete response-mode or response-signing need. | [OIDC Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html), [JARM](https://openid.net/specs/oauth-v2-jarm.html) |
| Weak client authentication patterns or broad redirect URI matching | Avoid wildcard or prefix-style redirect validation and avoid sending secrets in insecure channels. | [RFC 6749](https://www.rfc-editor.org/rfc/rfc6749), [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |
| Password grant style behavior | Not an OIDC authentication feature and not appropriate for a modern OP. | [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700) |

## 5. Practical Feature Set Recommendation

If the goal is a modern, interoperable, security-conscious OIDC IdP, the minimum practical target should be:

1. OIDC Core authorization code flow.
2. Discovery metadata and JWKS publication.
3. Authorization, token, and userinfo endpoints.
4. Standards-compliant ID Tokens and claim handling.
5. Exact redirect URI validation and robust client metadata management.
6. PKCE.
7. RP-initiated logout, with front-channel or back-channel logout when multi-app sign-out matters.
8. Revocation and introspection if opaque/reference tokens or operational revocation are required.
9. PAR, JAR, and sender-constrained tokens for higher-assurance deployments.
10. Dynamic registration, device flow, token exchange, CIBA, or federation only when the client ecosystem actually needs them.
11. Client Credentials Grant for machine-to-machine communication.
12. Refresh token rotation and reuse detection per RFC 9700 guidance.
13. JWT access token format (RFC 9068) when API consumers need standardized token validation without introspection.
14. Response mode support including `form_post` for server-side RPs.
15. WebAuthn/Passkeys for modern, phishing-resistant user authentication at the OP.
16. External IdP chaining and identity brokering when social or enterprise federation is needed.
17. Step-up authentication (RFC 9470) when APIs require dynamic assurance-level escalation.

## 6. Reference Index

### OpenID Foundation Specifications

- [OpenID Connect Core 1.0 incorporating errata set 2](https://openid.net/specs/openid-connect-core-1_0.html)
- [OpenID Connect Discovery 1.0 incorporating errata set 2](https://openid.net/specs/openid-connect-discovery-1_0.html)
- [OpenID Connect Dynamic Client Registration 1.0 incorporating errata set 2](https://openid.net/specs/openid-connect-registration-1_0.html)
- [OpenID Connect Session Management 1.0](https://openid.net/specs/openid-connect-session-1_0.html)
- [OpenID Connect Front-Channel Logout 1.0](https://openid.net/specs/openid-connect-frontchannel-1_0.html)
- [OpenID Connect Back-Channel Logout 1.0](https://openid.net/specs/openid-connect-backchannel-1_0.html)
- [OpenID Connect RP-Initiated Logout 1.0](https://openid.net/specs/openid-connect-rpinitiated-1_0.html)
- [OpenID Connect Client-Initiated Backchannel Authentication Flow - Core 1.0](https://openid.net/specs/openid-client-initiated-backchannel-authentication-core-1_0.html)
- [JWT Secured Authorization Response Mode for OAuth 2.0 (JARM)](https://openid.net/specs/oauth-v2-jarm.html)
- [OpenID Federation 1.0](https://openid.net/specs/openid-federation-1_0.html)
- [OpenID Connect for Identity Assurance 1.0](https://openid.net/specs/openid-connect-4-identity-assurance-1_0.html)
- [OpenID Shared Signals Framework 1.0](https://openid.net/specs/openid-sharedsignals-framework-1_0.html)
- [FAPI 2.0 Security Profile](https://openid.net/specs/fapi-security-profile-2_0.html)
- [OpenID Connect Self-Issued OpenID Provider v2](https://openid.net/specs/openid-connect-self-issued-v2-1_0.html)
- [OAuth 2.0 Form Post Response Mode](https://openid.net/specs/oauth-v2-form-post-response-mode-1_0.html)

### IETF RFCs

- [RFC 6749: The OAuth 2.0 Authorization Framework](https://www.rfc-editor.org/rfc/rfc6749)
- [RFC 7523: JSON Web Token (JWT) Profile for OAuth 2.0 Client Authentication and Authorization Grants](https://www.rfc-editor.org/rfc/rfc7523)
- [RFC 6750: The OAuth 2.0 Authorization Framework: Bearer Token Usage](https://www.rfc-editor.org/rfc/rfc6750)
- [RFC 7009: OAuth 2.0 Token Revocation](https://www.rfc-editor.org/rfc/rfc7009)
- [RFC 7515: JSON Web Signature (JWS)](https://www.rfc-editor.org/rfc/rfc7515)
- [RFC 7516: JSON Web Encryption (JWE)](https://www.rfc-editor.org/rfc/rfc7516)
- [RFC 7517: JSON Web Key (JWK)](https://www.rfc-editor.org/rfc/rfc7517)
- [RFC 7518: JSON Web Algorithms (JWA)](https://www.rfc-editor.org/rfc/rfc7518)
- [RFC 7519: JSON Web Token (JWT)](https://www.rfc-editor.org/rfc/rfc7519)
- [RFC 7591: OAuth 2.0 Dynamic Client Registration Protocol](https://www.rfc-editor.org/rfc/rfc7591)
- [RFC 7592: OAuth 2.0 Dynamic Client Registration Management Protocol](https://www.rfc-editor.org/rfc/rfc7592)
- [RFC 7636: Proof Key for Code Exchange by OAuth Public Clients](https://www.rfc-editor.org/rfc/rfc7636)
- [RFC 7662: OAuth 2.0 Token Introspection](https://www.rfc-editor.org/rfc/rfc7662)
- [RFC 8252: OAuth 2.0 for Native Apps](https://www.rfc-editor.org/rfc/rfc8252)
- [RFC 8414: OAuth 2.0 Authorization Server Metadata](https://www.rfc-editor.org/rfc/rfc8414)
- [RFC 8628: OAuth 2.0 Device Authorization Grant](https://www.rfc-editor.org/rfc/rfc8628)
- [RFC 8693: OAuth 2.0 Token Exchange](https://www.rfc-editor.org/rfc/rfc8693)
- [RFC 8705: OAuth 2.0 Mutual-TLS Client Authentication and Certificate-Bound Access Tokens](https://www.rfc-editor.org/rfc/rfc8705)
- [RFC 8707: Resource Indicators for OAuth 2.0](https://www.rfc-editor.org/rfc/rfc8707)
- [RFC 9101: JWT-Secured Authorization Request (JAR)](https://www.rfc-editor.org/rfc/rfc9101)
- [RFC 9126: OAuth 2.0 Pushed Authorization Requests (PAR)](https://www.rfc-editor.org/rfc/rfc9126)
- [RFC 9207: OAuth 2.0 Authorization Server Issuer Identification](https://www.rfc-editor.org/rfc/rfc9207)
- [RFC 9396: OAuth 2.0 Rich Authorization Requests](https://www.rfc-editor.org/rfc/rfc9396)
- [RFC 9449: OAuth 2.0 Demonstrating Proof of Possession (DPoP)](https://www.rfc-editor.org/rfc/rfc9449)
- [RFC 9470: OAuth 2.0 Step-Up Authentication Challenge Protocol](https://www.rfc-editor.org/rfc/rfc9470)
- [RFC 9700: Best Current Practice for OAuth 2.0 Security](https://www.rfc-editor.org/rfc/rfc9700)
- [RFC 9728: OAuth 2.0 Protected Resource Metadata](https://www.rfc-editor.org/rfc/rfc9728)
- [RFC 9068: JSON Web Token (JWT) Profile for OAuth 2.0 Access Tokens](https://www.rfc-editor.org/rfc/rfc9068)

## 7. How to Use This Document

- Use **Baseline Features** as the default implementation target for a general-purpose OIDC IdP.
- Use **Strongly Recommended Features** to define the production hardening backlog.
- Use **Optional or Advanced Features** to decide what to build for specific customers, regulated environments, or special client types.
- Use **Features Defined by Older Specs but Not Recommended** to avoid treating historical protocol support as a product goal by default.