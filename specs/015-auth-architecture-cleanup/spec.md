# Feature Specification: Auth Architecture Cleanup

**Feature Branch**: `015-auth-architecture-cleanup`  
**Created**: 2025-12-27  
**Status**: Draft  
**Input**: User description: "Follow the plan in architecture-refactoring-plan.md to refactor responsibilities and fix problems in our projects"

## Overview

This feature implements a comprehensive architectural refactoring of the MrWhoOidc solution to achieve clean separation between the OIDC engine (`MrWhoOidc.Auth`) and the HTTP/UI surface (`MrWhoOidc.WebAuth`). The refactoring addresses 8 layer violations, 3 god classes, 6 areas of code duplication, 5 security concerns, and 4 logic flaws identified in the architecture assessment.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Security Fixes for Token Operations (Priority: P1)

As a security-conscious system operator, I need critical security vulnerabilities fixed so that the OIDC provider operates safely under high load and validates tokens consistently.

**Why this priority**: Security issues directly impact system integrity and user trust. The identified blocking async call can cause denial of service, and inconsistent token validation creates security gaps.

**Independent Test**: Can be tested by running load tests against the token endpoint and verifying audience validation across both JWT and opaque token exchange paths.

**Acceptance Scenarios**:

1. **Given** a high-concurrency token request scenario (1000+ concurrent requests), **When** JWT tokens are created, **Then** the system maintains stable response times without threadpool starvation
2. **Given** an opaque access token with a specific audience, **When** that token is exchanged for a new token targeting a different audience, **Then** the exchange fails with appropriate error if audience is not in allowed list
3. **Given** concurrent consent grant requests for the same user/client, **When** both requests attempt to update consent, **Then** no data is lost due to race conditions

---

### User Story 2 - Layer Violation Corrections (Priority: P2)

As a developer maintaining the codebase, I need clear separation between Auth (domain logic) and WebAuth (HTTP handling) so that I can understand, test, and modify code in isolation.

**Why this priority**: Layer violations create tight coupling, making the codebase harder to maintain and test. This is foundational for future development velocity.

**Independent Test**: Can be verified by ensuring Auth project has no HTTP-specific dependencies and WebAuth handlers delegate to Auth services for all business logic.

**Acceptance Scenarios**:

1. **Given** the ClientAuthenticator component, **When** it authenticates a client, **Then** HTTP parameter extraction happens in WebAuth while credential validation logic resides in Auth
2. **Given** a user registration request, **When** the registration is processed, **Then** all domain logic (validation, account creation, email verification) is handled by Auth services
3. **Given** the OidcOptions configuration class, **When** Auth services need issuer/audience configuration, **Then** they access it from Auth.Options namespace without WebAuth dependency
4. **Given** a logout token needs to be created, **When** the logout flow executes, **Then** JWT creation is performed by Auth's token service, not WebAuth handlers

---

### User Story 3 - God Class Decomposition (Priority: P3)

As a developer working on token operations, I need the TokenService (723 lines) broken into focused, single-responsibility classes so that I can understand and modify specific behaviors without risk of side effects.

**Why this priority**: God classes slow down development, increase bug risk, and make code reviews difficult. Decomposition enables parallel development.

**Independent Test**: Can be verified by checking each new service class has a single responsibility, is under 150 lines, and has dedicated unit tests.

**Acceptance Scenarios**:

1. **Given** an authorization code exchange request, **When** tokens are generated, **Then** the operation is handled by a dedicated AuthorizationCodeExchanger service
2. **Given** a refresh token exchange request, **When** tokens are generated, **Then** the operation is handled by a dedicated RefreshTokenExchanger service
3. **Given** a client credentials grant request, **When** an M2M token is created, **Then** the operation is handled by a dedicated ClientCredentialsTokenFactory service
4. **Given** any token operation needs claims built, **When** claims are assembled, **Then** a dedicated AccessTokenClaimBuilder service handles claim construction

---

### User Story 4 - AuthorizeHandler Decomposition (Priority: P3)

As a developer working on authorization flows, I need the AuthorizeHandler (708 lines) broken into focused components so that each aspect of the authorization flow can be understood and tested independently.

**Why this priority**: The authorize endpoint is complex with many conditional paths. Breaking it down reduces cognitive load and bug surface area.

**Independent Test**: Can be verified by ensuring each component has dedicated tests and the main handler is under 200 lines acting as an orchestrator.

**Acceptance Scenarios**:

1. **Given** an authorization request with PAR/JAR parameters, **When** parameters are parsed, **Then** a dedicated AuthorizeRequestParser handles sanitization and resolution
2. **Given** an unauthenticated user with external IdP options, **When** provider selection occurs, **Then** a dedicated ProviderSelector component determines the appropriate IdP
3. **Given** consent is required for a user, **When** consent is checked and potentially displayed, **Then** a ConsentOrchestrator manages the flow
4. **Given** authorization is granted, **When** an authorization code is issued, **Then** a dedicated AuthorizationCodeIssuer handles code generation

---

### User Story 5 - Code Duplication Removal (Priority: P4)

As a developer maintaining the codebase, I need duplicated code consolidated into single implementations so that bug fixes and improvements apply everywhere consistently.

**Why this priority**: Duplication leads to inconsistent behavior and multiplied maintenance effort. While not security-critical, it impacts long-term maintainability.

**Independent Test**: Can be verified by grep searching for duplicated patterns and ensuring each utility exists in exactly one location.

**Acceptance Scenarios**:

1. **Given** token hash computation is needed, **When** any service computes a hash, **Then** it uses CryptoHelper directly without legacy wrapper methods
2. **Given** metrics need to be recorded for auth operations, **When** the metrics class is referenced, **Then** there is only one OidcMetrics class per project (GlobalAuthMetrics in Auth, OidcEndpointMetrics in WebAuth)
3. **Given** token lifetime needs to be determined, **When** any service calculates token expiry, **Then** it uses a single TokenLifetimeResolver that handles tenant→client→default cascade
4. **Given** role claims need to be built for a user, **When** any token includes roles, **Then** a single RoleClaimBuilder service constructs the claims

---

### Edge Cases

- What happens when a legacy client (using old single secret hash) authenticates during the migration period?
- How does the system handle concurrent token exchanges where the subject token is near expiry?
- What happens if the consent transaction fails mid-update?
- How does the system behave when a client has no realm configured but requests role claims?

## Requirements *(mandatory)*

### Functional Requirements

#### Phase 1: Security Fixes

- **FR-001**: System MUST perform key loading asynchronously in JwtService to prevent threadpool starvation under load
- **FR-002**: System MUST validate audience for opaque token exchange using the stored Audience field, consistent with JWT token validation
- **FR-003**: System MUST wrap consent grant operations in database transactions to prevent race conditions
- **FR-004**: System MUST emit metrics when legacy client secret authentication path is used to track migration progress

#### Phase 2: Layer Violations

- **FR-005**: System MUST provide IClientAuthenticationService interface in Auth layer for credential validation logic
- **FR-006**: System MUST implement IRegistrationService interface in Auth layer for user registration domain logic
- **FR-007**: System MUST define OidcOptions in Auth.Options namespace, removing circular dependency on WebAuth
- **FR-008**: System MUST implement logout token creation in Auth's token services, not in WebAuth handlers

#### Phase 3: God Class Decomposition

- **FR-009**: System MUST provide IAuthorizationCodeExchanger service handling only authorization code → token exchange
- **FR-010**: System MUST provide IRefreshTokenExchanger service handling only refresh token → token exchange
- **FR-011**: System MUST provide IClientCredentialsTokenFactory service handling only M2M token creation
- **FR-012**: System MUST provide IAccessTokenClaimBuilder service for constructing access token claims
- **FR-013**: System MUST provide IAuthorizeRequestParser in Auth layer for parameter parsing and sanitization
- **FR-014**: System MUST reduce TokenService orchestrator to under 150 lines delegating to specialized services
- **FR-015**: System MUST reduce AuthorizeHandler to under 200 lines acting as HTTP orchestrator only

#### Phase 4: Duplication Removal

- **FR-016**: System MUST use CryptoHelper directly for all hash computations, removing legacy wrapper methods
- **FR-017**: System MUST rename Auth's OidcMetrics to GlobalAuthMetrics to avoid naming collision
- **FR-018**: System MUST provide ITokenLifetimeResolver for centralized token lifetime calculation
- **FR-019**: System MUST provide IRoleClaimBuilder for centralized role claim construction
- **FR-020**: System MUST provide IOpaqueTokenPolicy for centralized opaque token decision logic
- **FR-021**: System MUST provide IMtlsThumbprintResolver for centralized mTLS thumbprint lookup

#### Phase 5: Code Quality

- **FR-022**: All public interfaces in Auth MUST have XML documentation
- **FR-023**: All services in Auth MUST use C# nullable reference type annotations
- **FR-024**: All new services MUST have corresponding unit tests with at least 80% code coverage

### Key Entities

- **IClientAuthenticationService**: Abstraction for client credential validation, implemented in Auth, consumed by WebAuth's HTTP layer
- **IAuthorizationCodeExchanger**: Handles authorization code to token exchange, encapsulates PKCE validation and code consumption
- **IRefreshTokenExchanger**: Handles refresh token rotation and new token issuance
- **IClientCredentialsTokenFactory**: Creates M2M tokens for client credentials grant
- **IAccessTokenClaimBuilder**: Constructs claims for access tokens including roles, scopes, tenant, entitlements
- **ITokenLifetimeResolver**: Resolves token lifetime from tenant settings → client settings → defaults
- **IRoleClaimBuilder**: Builds role claims from user realm and client role assignments
- **GlobalAuthMetrics**: Metrics for core authentication operations (renamed from OidcMetrics in Auth)

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All existing unit tests continue to pass after refactoring (100% backward compatibility for tests)
- **SC-002**: Token endpoint maintains current response times under 1000 concurrent requests (no regression)
- **SC-003**: TokenService reduced from 723 lines to under 150 lines (80% reduction)
- **SC-004**: AuthorizeHandler reduced from 708 lines to under 200 lines (72% reduction)
- **SC-005**: No Auth project files import from MrWhoOidc.WebAuth namespace (zero layer violations)
- **SC-006**: All security-critical services have at least 90% unit test coverage
- **SC-007**: Code duplication scanner reports zero instances of identified duplicate patterns
- **SC-008**: All new service interfaces have XML documentation (100% public API documented)

## Assumptions

- The existing test suite provides adequate coverage to detect regressions during refactoring
- Breaking changes to internal interfaces are acceptable as this is an internal refactoring (no public API changes)
- The refactoring can be done incrementally, phase by phase, with working code at each phase boundary
- Developer tooling (IDE, build system) supports file moves and namespace refactoring
- The 8-week timeline in the architecture plan is aspirational; phases may be adjusted based on findings

## Dependencies

- Existing architecture-refactoring-plan.md provides detailed technical guidance
- EF Core migrations infrastructure must remain functional throughout refactoring
- Redis integration for distributed caching must not be disrupted
- Multi-tenancy functionality must remain operational

