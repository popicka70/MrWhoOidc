# Feature Specification: Key and License Management Service

**Feature Branch**: `001-key-license-generator`  
**Created**: October 28, 2025  
**Status**: Draft  
**Input**: User description: "I want to develop a service web app that would generate secret/public keys for our OIDC server and clients to be used with JAR/JARM. As of now we have such support included in the OIDC server but that is not correct since we generate private key at place that should ever only see the public key. So i want to have the generator separated to a spcialized app. The goal is to have the app run in docker. We also need incorporate licence generator. We have it as command line utility but web UI would be nicer. So the goal is to have it in docker with WEB UI. Razor or Blazor UI based on your recommendation. Then we'll able to cleanup the OIDC sever and remove unneeded/misplaced functionality."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Key Pair Generation for OIDC Clients (Priority: P1)

As an administrator, I need to generate cryptographic key pairs (RSA, ECDSA) for OIDC clients to use with JAR (JWT-secured Authorization Requests) and JARM (JWT-secured Authorization Response Mode), so that clients can sign authorization requests and decrypt authorization responses without exposing private keys to the authorization server.

**Why this priority**: This is the primary security issue motivating the feature. The authorization server currently generates private keys it should never possess. Fixing this architectural flaw is critical for security compliance and proper separation of concerns.

**Independent Test**: Can be fully tested by generating a key pair via the web UI, downloading both keys, verifying the private key can sign a JWT, and confirming the corresponding public key (in JWKS format) validates the signature when registered with the OIDC server.

**Acceptance Scenarios**:

1. **Given** I am an administrator, **When** I access the key generation page and select RSA 2048-bit with RS256 algorithm, **Then** the system generates a matching public/private key pair with a unique key ID (kid).
2. **Given** a key pair has been generated, **When** I download the private key, **Then** I receive it in JWK format suitable for client-side signing.
3. **Given** a key pair has been generated, **When** I download the public key, **Then** I receive it in JWKS format ready to be registered in the OIDC server's client configuration.
4. **Given** I select ECDSA algorithm, **When** I choose ES256, ES384, or ES512, **Then** the system generates the appropriate elliptic curve key pair (P-256, P-384, P-521).
5. **Given** multiple key pairs exist, **When** I view the key management page, **Then** I see a list of all generated keys with their algorithm, kid, creation date, and usage count.

---

### User Story 2 - License Token Generation with Web UI (Priority: P2)

As an administrator, I need to generate license tokens through a web interface rather than command-line tools, so that I can easily create licenses for different organizations with specific tiers, features, limits, and expiry dates.

**Why this priority**: This improves operational efficiency by replacing the command-line tool with a user-friendly web interface. While important, it's secondary to fixing the key generation security issue.

**Independent Test**: Can be fully tested by creating a license token through the web UI with specific parameters (tier=enterprise, organization=TestCorp, valid-days=365, features=analytics,dpop), downloading the generated JWT, and validating it contains the correct claims when decoded.

**Acceptance Scenarios**:

1. **Given** I am an administrator, **When** I access the license generation form and provide tier (enterprise/professional/community), organization name, validity period, features, and limits, **Then** the system generates a signed license JWT.
2. **Given** a license has been generated, **When** I download the license token, **Then** I receive a JWT file containing all specified claims signed with the licensing private key.
3. **Given** I specify multiple features (e.g., analytics, dpop, multi-tenant), **When** I generate the license, **Then** the JWT includes all features in the "features" array claim.
4. **Given** I set limits (e.g., tenants=50, users=1000), **When** I generate the license, **Then** the JWT includes a "limits" object with each key-value pair.
5. **Given** I provide a validity period, **When** I generate the license, **Then** the JWT includes correct nbf, iat, and exp claims reflecting the specified date range.

---

### User Story 3 - Key Lifecycle Management (Priority: P3)

As an administrator, I need to view, track, and delete generated keys, so that I can maintain an audit trail of cryptographic material and remove keys that are no longer needed or have been compromised.

**Why this priority**: This provides operational visibility and compliance support. While valuable, basic generation capability (P1) must exist first.

**Independent Test**: Can be fully tested by generating multiple keys, verifying they appear in the key list with metadata, marking one as revoked or deleting it, and confirming it no longer appears in active key listings.

**Acceptance Scenarios**:

1. **Given** multiple key pairs have been generated, **When** I view the key management dashboard, **Then** I see each key's algorithm, kid, creation timestamp, and status (active/revoked).
2. **Given** a key has been compromised or is no longer needed, **When** I mark it as revoked, **Then** the system updates its status and displays a revocation timestamp.
3. **Given** I need to audit key usage, **When** I view key details, **Then** I see when it was created, by whom, and how many times the public key was downloaded.
4. **Given** a key has been revoked, **When** I attempt to download its private key, **Then** the system prevents the download and displays a warning message.

---

### Edge Cases

- What happens when a user attempts to generate a key with an unsupported algorithm? System displays an error message listing supported algorithms (RS256, RS384, RS512, ES256, ES384, ES512, PS256).
- How does the system handle concurrent key generation requests? Each request generates a unique kid using GUIDs to prevent collisions; generation is stateless and can handle parallel requests.
- What happens if the licensing private key is missing or invalid? System displays a clear error on startup and prevents license generation functionality until key is properly configured.
- How does the system prevent unauthorized access? All endpoints require authentication; deployment documentation specifies that the service should run in a secure internal network or behind authentication middleware.
- What happens when downloading large JWKS files? System supports pagination or filtering for key lists; individual key downloads remain small (< 5KB per key).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST generate RSA key pairs in 2048-bit, 3072-bit, and 4096-bit sizes.
- **FR-002**: System MUST generate ECDSA key pairs using P-256, P-384, and P-521 curves.
- **FR-003**: System MUST assign a unique key identifier (kid) to each generated key pair using GUID format.
- **FR-004**: System MUST export private keys in JWK format suitable for client-side JWT signing.
- **FR-005**: System MUST export public keys in JWKS format compatible with OIDC server client configuration.
- **FR-006**: System MUST generate license tokens as signed JWTs containing tier, organization, validity dates, features array, and limits object.
- **FR-007**: System MUST sign license tokens using ECDSA P-256 with the configured licensing private key.
- **FR-008**: System MUST support configuring license validity through "valid-from", "valid-until", or "valid-days" parameters.
- **FR-009**: System MUST allow specifying multiple features (e.g., analytics, dpop, multi-tenant) and multiple limits (e.g., tenants=50, users=1000) for license tokens.
- **FR-010**: System MUST persist metadata for generated keys including algorithm, kid, creation timestamp, and status.
- **FR-011**: System MUST provide a web interface for key generation, license generation, and key management operations.
- **FR-012**: System MUST support marking keys as revoked and preventing download of revoked private keys.
- **FR-013**: System MUST run as a containerized Docker application.
- **FR-014**: System MUST load the licensing private key from a secure location (mounted volume or secret) at startup.
- **FR-015**: System MUST support both RS256/RS384/RS512 (RSA-PSS) and ES256/ES384/ES512 (ECDSA) signing algorithms for generated keys.

### Key Entities *(include if feature involves data)*

- **KeyPairMetadata**: Represents a generated cryptographic key pair including kid (unique identifier), algorithm (RS256, ES256, etc.), creation timestamp, status (active/revoked), creator identifier, and download count.
- **LicenseToken**: Represents a generated license JWT including tier (community/professional/enterprise), organization name, validity period (nbf, iat, exp), features array, limits dictionary, issuer, and token ID (jti).
- **KeyDownloadRecord**: Tracks when keys were downloaded including kid, download timestamp, download type (private/public), and requester identifier for audit purposes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Administrators can generate a complete RSA or ECDSA key pair and download both keys in under 10 seconds.
- **SC-002**: Generated private keys can successfully sign JWT payloads that are validated by standard OIDC implementations.
- **SC-003**: Generated public keys in JWKS format can be imported into the OIDC server's client configuration without manual modification.
- **SC-004**: Administrators can generate a license token with custom parameters and download it in under 15 seconds.
- **SC-005**: Generated license tokens pass validation when deployed to the OIDC server using the corresponding public key.
- **SC-006**: The service starts successfully in Docker and remains responsive with startup time under 30 seconds.
- **SC-007**: The web interface is accessible without errors on modern browsers (Chrome, Firefox, Edge, Safari).
- **SC-008**: Key metadata is persisted and survives container restarts when using persistent storage.
- **SC-009**: The OIDC server's key generation functionality can be removed after deploying this service, reducing its codebase and attack surface.
- **SC-010**: 100% of generated keys include all required JWK parameters (kty, kid, alg, and algorithm-specific parameters like n/e for RSA or crv/x/y for EC).

## Assumptions *(optional)*

- The service will be deployed in a secure internal network or behind authentication middleware (e.g., corporate SSO, VPN).
- Administrators have sufficient cryptographic knowledge to understand the difference between public and private keys and handle them appropriately.
- The licensing private key (ECDSA P-256 PEM format) is provided externally and mounted into the container at deployment time.
- Persistent storage for key metadata is provided via Docker volumes or equivalent container orchestration storage mechanisms.
- The existing command-line license generator will remain available during a transition period but will be deprecated once the web UI is validated.
- The service does not need to support key rotation or automatic expiry; administrators manually manage key lifecycle.
- The OIDC server's client configuration already supports importing JWKS JSON for JAR/JARM keys; no server-side changes are needed beyond removing the misplaced key generation code.

## Dependencies *(optional)*

- Docker runtime environment for containerized deployment.
- Existing licensing private key in PEM format for signing license tokens.
- Persistent storage mechanism (Docker volumes, Kubernetes PVCs, or equivalent) for key metadata database.
- The OIDC server must support importing client JWKS for JAR/JARM validation (already implemented per codebase analysis).

## Out of Scope *(optional)*

- Automatic key rotation or scheduled key expiry.
- Integration with hardware security modules (HSM) or cloud key management services (KMS).
- Multi-user authentication and role-based access control within the service itself (delegated to deployment environment).
- Real-time key revocation propagation to clients; administrators must manually update client configurations.
- License validation logic (handled by the OIDC server; this service only generates tokens).
- Key escrow or backup/recovery mechanisms for lost private keys.
- Support for symmetric keys (HMAC) for JAR; clients should use asymmetric keys for better security.
- PEM format export for keys; JWK/JWKS JSON format is standard for OIDC use cases.

## Notes *(optional)*

### UI Framework Recommendation

Razor Pages is recommended over Blazor for this service because:

1. **Simplicity**: The UI consists of simple forms with minimal interactivity (generate, download, list).
2. **Performance**: Razor Pages have lower overhead and faster initial load times for form-based workflows.
3. **Consistency**: The existing MrWhoOidc.WebAuth project uses Razor Pages extensively; maintaining consistency reduces cognitive load.
4. **Docker footprint**: Razor Pages result in smaller container images compared to Blazor with WebAssembly dependencies.

### Security Considerations

- The service must NEVER transmit or store private keys in logs, telemetry, or database records beyond the immediate generation and download response.
- Private key downloads should be one-time operations with secure deletion from memory after transmission.
- The licensing private key must be loaded from a secure mount (e.g., Docker secret, Kubernetes secret) and never hardcoded in configuration files.
- Consider implementing download tokens or short-lived URLs for private key retrieval to prevent accidental leakage via browser history or logs.

### Migration Path

Once this service is deployed and validated:

1. Remove `OnPostGenerateJwksAsync()` and `OnPostAddKeyAsync()` methods from `MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs`.
2. Remove `GeneratedPrivateJwk` property and related UI elements from the client edit page.
3. Update documentation to direct administrators to the new key generator service.
4. Deprecate but retain the command-line `LicenseGenerator` tool for 1-2 release cycles to ensure smooth transition.
5. Add health checks and monitoring to the key generator service to ensure it's operational before removing OIDC server functionality.
