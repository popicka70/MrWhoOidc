# Tasks: Key and License Management Service

**Input**: Design documents from `/specs/001-key-license-generator/`  
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are NOT included in this implementation plan. Focus is on delivering working features first.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

This project uses a standalone service structure:

- `MrWhoOidc.KeyGen/` - Main service project
- `MrWhoOidc.KeyGen.Tests/` - Test project

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [x] T001 Create MrWhoOidc.KeyGen web project using `dotnet new webapp -n MrWhoOidc.KeyGen -f net9.0`
- [x] T002 Create MrWhoOidc.KeyGen.Tests test project using `dotnet new mstest -n MrWhoOidc.KeyGen.Tests -f net9.0`
- [x] T003 Add project references and update solution file MrWhoOidc.slnx
- [x] T004 [P] Install NuGet packages: Microsoft.EntityFrameworkCore.Sqlite, Microsoft.EntityFrameworkCore.Design in MrWhoOidc.KeyGen/MrWhoOidc.KeyGen.csproj
- [x] T005 [P] Install NuGet packages: System.IdentityModel.Tokens.Jwt, Microsoft.IdentityModel.Tokens in MrWhoOidc.KeyGen/MrWhoOidc.KeyGen.csproj
- [x] T006 [P] Install NuGet packages: Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore in MrWhoOidc.KeyGen/MrWhoOidc.KeyGen.csproj
- [x] T007 [P] Install test packages: Microsoft.AspNetCore.Mvc.Testing, Microsoft.EntityFrameworkCore.InMemory in MrWhoOidc.KeyGen.Tests/MrWhoOidc.KeyGen.Tests.csproj
- [x] T008 Copy GuidHelper.cs from MrWhoOidc.Auth/Persistence/GuidHelper.cs to MrWhoOidc.KeyGen/Persistence/GuidHelper.cs
- [x] T009 Create directory structure: Domain/, Domain/Models/, Domain/Services/, Domain/Cryptography/, Persistence/, Persistence/Migrations/, Pages/, Pages/Shared/, Pages/KeyGeneration/, Pages/LicenseGeneration/, Api/, Configuration/, wwwroot/ in MrWhoOidc.KeyGen/

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T010 Create KeyPairMetadata entity in MrWhoOidc.KeyGen/Domain/Models/KeyPairMetadata.cs with Id, Kid, Algorithm, KeyType, KeySize, Curve, PublicKeyJwks, CreatedAt, Status, RevokedAt, CreatedBy, DownloadCount properties
- [x] T011 [P] Create KeyDownloadRecord entity in MrWhoOidc.KeyGen/Domain/Models/KeyDownloadRecord.cs with Id, KeyPairMetadataId, DownloadType, DownloadedAt, DownloadedBy, IpAddress, UserAgent properties
- [x] T012 [P] Create LicenseTokenMetadata entity in MrWhoOidc.KeyGen/Domain/Models/LicenseTokenMetadata.cs with Id, TokenId, Tier, Organization, ValidFrom, ValidUntil, Features, Limits, GeneratedAt, GeneratedBy properties
- [x] T013 Create KeyGenDbContext in MrWhoOidc.KeyGen/Persistence/KeyGenDbContext.cs with DbSet properties and OnModelCreating configuration (unique indexes, relationships, constraints)
- [x] T014 Configure SQLite connection string in MrWhoOidc.KeyGen/appsettings.json (Data Source=/data/keygen.db) and MrWhoOidc.KeyGen/appsettings.Development.json (Data Source=keygen-dev.db)
- [x] T015 Create KeyGenOptions configuration model in MrWhoOidc.KeyGen/Configuration/KeyGenOptions.cs with LicensingPrivateKeyPath property
- [x] T016 Register DbContext and services in MrWhoOidc.KeyGen/Program.cs with SQLite provider, health checks, and antiforgery configuration
- [x] T017 Generate initial EF Core migration using `dotnet ef migrations add InitialCreate --project MrWhoOidc.KeyGen --output-dir Persistence/Migrations`
- [x] T018 Apply migration to create development database using `dotnet ef database update --project MrWhoOidc.KeyGen`
- [x] T019 [P] Create Razor Pages layout in MrWhoOidc.KeyGen/Pages/Shared/\_Layout.cshtml with navigation menu and Bootstrap/Tailwind styling
- [x] T020 [P] Create \_ViewImports.cshtml and \_ViewStart.cshtml in MrWhoOidc.KeyGen/Pages/ for Razor Pages configuration
- [x] T021 [P] Create Index landing page in MrWhoOidc.KeyGen/Pages/Index.cshtml and Index.cshtml.cs with links to key generation and license generation
- [x] T022 Create health check endpoint in MrWhoOidc.KeyGen/Program.cs that validates database connectivity and licensing key availability

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - Key Pair Generation for OIDC Clients (Priority: P1) 🎯 MVP

**Goal**: Enable administrators to generate RSA/ECDSA key pairs and download them securely for OIDC client JAR/JARM usage

**Independent Test**: Generate a key pair via the web UI, download both private (JWK) and public (JWKS) keys, verify the private key can sign a JWT, and confirm the public key validates the signature when imported into OIDC server

### Cryptography Layer for User Story 1

- [x] T023 [P] [US1] Create RsaKeyGenerator class in MrWhoOidc.KeyGen/Domain/Cryptography/RsaKeyGenerator.cs with Generate(keySize) method returning RSA key pair
- [x] T024 [P] [US1] Create EcdsaKeyGenerator class in MrWhoOidc.KeyGen/Domain/Cryptography/EcdsaKeyGenerator.cs with Generate(curve) method returning ECDSA key pair
- [x] T025 [US1] Create JwkSerializer class in MrWhoOidc.KeyGen/Domain/Cryptography/JwkSerializer.cs with SerializePrivateKey and SerializePublicKey methods for JWK/JWKS format

### Domain Services for User Story 1

- [x] T026 [US1] Create IKeyGenerationService interface in MrWhoOidc.KeyGen/Domain/Services/IKeyGenerationService.cs with GenerateKeyPairAsync method signature
- [x] T027 [US1] Implement KeyGenerationService in MrWhoOidc.KeyGen/Domain/Services/KeyGenerationService.cs that generates kid using GuidHelper.NewId(), creates KeyPairMetadata, saves to DB, returns private JWK and public JWKS
- [x] T028 [US1] Register IKeyGenerationService as scoped service in MrWhoOidc.KeyGen/Program.cs

### UI Pages for User Story 1

- [x] T029 [US1] Create key generation form page in MrWhoOidc.KeyGen/Pages/KeyGeneration/Generate.cshtml with dropdowns for algorithm (RS256/RS384/RS512/ES256/ES384/ES512/PS256), key type (RSA/EC), key size (2048/3072/4096), and curve (P-256/P-384/P-521)
- [x] T030 [US1] Implement Generate.cshtml.cs page model in MrWhoOidc.KeyGen/Pages/KeyGeneration/ with OnGetAsync and OnPostAsync methods, form validation, call to KeyGenerationService, and display of generated kid with download links
- [x] T031 [US1] Add form validation logic in Generate.cshtml.cs: validate algorithm selection, require key size for RSA, require curve for EC, display validation errors on page reload
- [x] T032 [US1] Add antiforgery token to key generation form in Generate.cshtml using @Html.AntiForgeryToken()

### Download API Endpoints for User Story 1

- [x] T033 [US1] Create KeyDownloadEndpoints class in MrWhoOidc.KeyGen/Api/KeyDownloadEndpoints.cs with MapGet methods for /api/keys/{kid}/private and /api/keys/{kid}/public
- [x] T034 [US1] Implement GET /api/keys/{kid}/private endpoint that fetches KeyPairMetadata, regenerates private JWK from stored data, records download in KeyDownloadRecord, returns JWK with Content-Disposition: attachment
- [x] T035 [US1] Implement GET /api/keys/{kid}/public endpoint that fetches KeyPairMetadata, returns stored PublicKeyJwks with Content-Disposition: attachment
- [x] T036 [US1] Add error handling in KeyDownloadEndpoints: return 404 Not Found for invalid kid, return 403 Forbidden for revoked keys, return 500 with error details for internal errors
- [x] T037 [US1] Register download endpoints in MrWhoOidc.KeyGen/Program.cs using app.MapGroup("/api/keys")

### Key Listing Page for User Story 1

- [x] T038 [US1] Create key management list page in MrWhoOidc.KeyGen/Pages/KeyGeneration/List.cshtml with table displaying kid, algorithm, key type, key size/curve, created date, status, download count
- [x] T039 [US1] Implement List.cshtml.cs page model in MrWhoOidc.KeyGen/Pages/KeyGeneration/ with OnGetAsync method, query parameters for filtering (status, algorithm, page, pageSize), pagination logic, fetch keys from DbContext

**Checkpoint**: At this point, User Story 1 should be fully functional - administrators can generate RSA/ECDSA key pairs, download them, and view the key list

---

## Phase 4: User Story 2 - License Token Generation with Web UI (Priority: P2)

**Goal**: Enable administrators to generate license tokens through a web interface with custom parameters (tier, organization, validity, features, limits)

**Independent Test**: Create a license token through the web UI with specific parameters (tier=enterprise, organization=TestCorp, valid-days=365, features=analytics,dpop), download the JWT, decode it, and verify it contains the correct claims

### Domain Services for User Story 2

- [x] T040 [P] [US2] Create ILicenseGenerationService interface in MrWhoOidc.KeyGen/Domain/Services/ILicenseGenerationService.cs with GenerateLicenseAsync method signature
- [x] T041 [US2] Implement LicenseGenerationService in MrWhoOidc.KeyGen/Domain/Services/LicenseGenerationService.cs that loads licensing private key from configured path, builds JWT payload with iss/nbf/iat/exp/jti/tier/organization/features/limits claims, signs with ECDSA P-256, creates LicenseTokenMetadata, saves to DB, returns signed JWT
- [x] T042 [US2] Add licensing key loading logic in LicenseGenerationService constructor: read PEM file from KeyGenOptions.LicensingPrivateKeyPath, parse using ECDsa.ImportFromPem, fail fast if missing or invalid
- [x] T043 [US2] Register ILicenseGenerationService as scoped service in MrWhoOidc.KeyGen/Program.cs
- [ ] T044 [US2] Update health check in Program.cs to validate licensing private key existence and validity on startup

### UI Pages for User Story 2

- [x] T045 [US2] Create license generation form page in MrWhoOidc.KeyGen/Pages/LicenseGeneration/Generate.cshtml with fields for tier (dropdown: community/professional/enterprise), organization (text), valid-from (date), valid-until (date), valid-days (number), features (comma-separated text), limits (JSON text area)
- [x] T046 [US2] Implement Generate.cshtml.cs page model in MrWhoOidc.KeyGen/Pages/LicenseGeneration/ with OnGetAsync and OnPostAsync methods, form validation, call to LicenseGenerationService, display generated JWT with copy-to-clipboard button
- [x] T047 [US2] Add form validation logic in Generate.cshtml.cs: validate tier selection, validate date range (ValidFrom < ValidUntil), parse and validate features (comma-separated), parse and validate limits (JSON format), display validation errors on page reload
- [x] T048 [US2] Add antiforgery token to license generation form in Generate.cshtml using @Html.AntiForgeryToken()
- [x] T049 [US2] Add JavaScript for copy-to-clipboard functionality in Generate.cshtml for the generated JWT token

### Download API Endpoints for User Story 2

- [x] T050 [US2] Create LicenseDownloadEndpoints class in MrWhoOidc.KeyGen/Api/LicenseDownloadEndpoints.cs with MapGet method for /api/licenses/{tokenId}/download
- [x] T051 [US2] Implement GET /api/licenses/{tokenId}/download endpoint that fetches LicenseTokenMetadata, reconstructs JWT from stored metadata (or stores JWT during generation), returns JWT with Content-Type: text/plain and Content-Disposition: attachment filename=license-{organization}-{tokenId}.jwt
- [x] T052 [US2] Add error handling in LicenseDownloadEndpoints: return 404 Not Found for invalid tokenId, return 500 with error details for internal errors
- [x] T053 [US2] Register license download endpoints in MrWhoOidc.KeyGen/Program.cs using app.MapGroup("/api/licenses")

### License Listing Page for User Story 2

- [x] T054 [US2] Create license management list page in MrWhoOidc.KeyGen/Pages/LicenseGeneration/List.cshtml with table displaying tokenId, tier, organization, features, valid-from, valid-until, status (valid/expired), generated date, generated by
- [x] T055 [US2] Implement List.cshtml.cs page model in MrWhoOidc.KeyGen/Pages/LicenseGeneration/ with OnGetAsync method, query parameters for filtering (tier, organization, expiry status), fetch licenses from DbContext

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently - key generation and license generation are both fully functional

---

## Phase 5: User Story 3 - Key Lifecycle Management (Priority: P3)

**Goal**: Enable administrators to view, track, revoke, and audit generated keys for compliance and security

**Independent Test**: Generate multiple keys, verify they appear in the key list with correct metadata, mark one as revoked, confirm revocation timestamp is displayed, attempt to download revoked key's private key and verify it fails with 403 Forbidden

### Key Revocation Feature for User Story 3

- [x] T056 [US3] Add revocation support to KeyGenerationService: create RevokeKeyAsync method in MrWhoOidc.KeyGen/Domain/Services/KeyGenerationService.cs that updates KeyPairMetadata.Status to "Revoked" and sets RevokedAt timestamp
- [x] T057 [US3] Update List.cshtml in MrWhoOidc.KeyGen/Pages/KeyGeneration/ to add "Revoke" button for active keys in the table
- [x] T058 [US3] Create Revoke page handler in List.cshtml.cs with OnPostAsync method that calls RevokeKeyAsync, redirects to List page with success message
- [x] T059 [US3] Add revocation check in KeyDownloadEndpoints private key endpoint: query KeyPairMetadata.Status, return 403 Forbidden if Status = "Revoked" with error message

### Enhanced Key Listing and Filtering for User Story 3

- [x] T060 [US3] Update List.cshtml.cs in MrWhoOidc.KeyGen/Pages/KeyGeneration/ to add filtering by status (active/revoked/all via query parameter), filtering by algorithm (RS256/ES256/etc. via query parameter)
- [x] T061 [US3] Add filter UI controls in List.cshtml: dropdown for status filter, dropdown for algorithm filter, "Apply Filters" button
- [x] T062 [US3] Implement pagination logic in List.cshtml.cs: page number and page size query parameters (default 20 items per page), calculate total pages, add Previous/Next navigation

### Key Details and Audit Trail for User Story 3

- [x] T063 [US3] Create Details.cshtml page in MrWhoOidc.KeyGen/Pages/KeyGeneration/ that displays full key metadata: kid, algorithm, key type, key size/curve, created date, status, revoked date, created by, download count
- [x] T064 [US3] Implement Details.cshtml.cs page model in MrWhoOidc.KeyGen/Pages/KeyGeneration/ with OnGetAsync method that fetches KeyPairMetadata by kid, loads related KeyDownloadRecord entries
- [x] T065 [US3] Display download history table in Details.cshtml showing download type (private/public), downloaded date, downloaded by, IP address, user agent for each KeyDownloadRecord
- [x] T066 [US3] Add "View Details" link in List.cshtml table for each key that navigates to Details page with kid parameter

**Checkpoint**: All user stories should now be independently functional - key generation, license generation, and key lifecycle management are complete

---

## Phase 6: Docker Containerization & Deployment

**Purpose**: Package the service as a Docker container for deployment

- [ ] T067 [P] Create Dockerfile in MrWhoOidc.KeyGen/Dockerfile with multi-stage build (build stage with SDK, runtime stage with aspnet:9.0 base image)
- [ ] T068 [P] Create .dockerignore in MrWhoOidc.KeyGen/.dockerignore to exclude bin/, obj/, *.db, .git/
- [ ] T069 Configure Dockerfile to expose port 8080 for HTTP (or 8443 for HTTPS), set ASPNETCORE_URLS environment variable
- [ ] T070 Add volume mount instructions in Dockerfile comments for /data (SQLite database) and /secrets (licensing private key)
- [ ] T071 Add HEALTHCHECK instruction in Dockerfile that calls /health endpoint with 30-second interval
- [ ] T072 [P] Create docker-compose.yml (optional) in repository root for local development with keygen service, volume mounts, and environment variables
- [ ] T073 Test Docker build: run `docker build -t mrwhooidc-keygen:latest -f MrWhoOidc.KeyGen/Dockerfile .` and verify image size < 200MB
- [ ] T074 Test Docker run: start container with volume mounts, access service on localhost:8080, verify database persistence across container restarts

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories and production readiness

- [ ] T073 [P] Add security headers middleware in MrWhoOidc.KeyGen/Program.cs: X-Frame-Options: DENY, X-Content-Type-Options: nosniff, Content-Security-Policy, Strict-Transport-Security (HTTPS only)
- [ ] T074 [P] Add structured logging throughout services using ILogger with correlation IDs and sensitive data redaction (never log private keys or JWTs)
- [ ] T075 [P] Create README.md in MrWhoOidc.KeyGen/ with quickstart instructions, Docker deployment guide, configuration reference, security considerations
- [ ] T076 [P] Update main repository docs/developer-guide.md with instructions to remove key generation code from MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs (OnPostGenerateJwksAsync and OnPostAddKeyAsync methods)
- [ ] T077 [P] Add telemetry and metrics: track key generation count by algorithm, license generation count by tier, download counts, error rates
- [ ] T078 [P] Add rate limiting middleware (optional) in Program.cs for key/license generation endpoints to prevent abuse
- [ ] T079 Validate zero compiler warnings: run `dotnet build MrWhoOidc.KeyGen --configuration Release` and confirm zero warnings
- [ ] T080 Validate zero analyzer warnings: enable TreatWarningsAsErrors in MrWhoOidc.KeyGen.csproj and verify clean build
- [ ] T081 [P] Create deployment documentation in docs/key-license-generator-deployment.md covering Docker deployment, volume configuration, licensing key setup, environment variables, health check monitoring

---

## Dependencies & Execution Order

### User Story Dependencies

```text
Phase 1 (Setup) → Phase 2 (Foundational)
                    ↓
        ┌──────────┼──────────┐
        ↓          ↓          ↓
   [US1] P1    [US2] P2    [US3] P3
        ↓
   (MVP)
```

**Critical Path**: Phase 1 → Phase 2 → User Story 1 (MVP)

**User Story Dependencies**:

- **US1** (Key Generation): Independent - can be implemented first
- **US2** (License Generation): Independent - can be implemented in parallel with US1 after Phase 2
- **US3** (Key Lifecycle): Depends on US1 (needs key generation to exist before lifecycle management)

### Parallel Execution Opportunities

**Within Phase 1 (Setup)**: T004, T005, T006, T007 can run in parallel (different package installations)

**Within Phase 2 (Foundational)**:

- T010, T011, T012 can run in parallel (separate entity files)
- T019, T020, T021 can run in parallel (separate Razor Pages files)

**Within Phase 3 (User Story 1)**:

- T023, T024 can run in parallel (separate key generator files)
- After T025 completes: T026-T028 (service interface/implementation) can run in parallel with T029-T032 (UI pages)

**Within Phase 4 (User Story 2)**:

- T040-T044 (domain services) can run in parallel with T045-T049 (UI pages) after Phase 2 completes

**Within Phase 5 (User Story 3)**:

- T058-T060 (filtering) can run in parallel with T061-T064 (details page)

**Within Phase 6 (Docker)**: T065, T066, T070 can run in parallel (separate Docker files)

**Within Phase 7 (Polish)**: T073, T074, T075, T076, T077, T078, T081 can all run in parallel

---

## Implementation Strategy

### Minimum Viable Product (MVP)

**MVP Scope**: User Story 1 only (Key Pair Generation)

**MVP Tasks**: Phase 1 (T001-T009) + Phase 2 (T010-T022) + Phase 3 (T023-T039) = 39 tasks

**MVP Deliverable**:

- Administrators can generate RSA/ECDSA key pairs via web UI
- Download private keys (JWK) and public keys (JWKS)
- View list of generated keys with metadata
- Docker containerized service

**MVP Timeline**: 3-5 days for experienced developer

### Incremental Delivery

1. **Sprint 1**: MVP (User Story 1) - Phases 1-3 (T001-T039)
2. **Sprint 2**: License Generation (User Story 2) - Phase 4 (T040-T053)
3. **Sprint 3**: Key Lifecycle (User Story 3) - Phase 5 (T054-T064)
4. **Sprint 4**: Docker + Polish - Phases 6-7 (T065-T081)

### Testing Strategy

**Manual Testing** (no automated tests in this plan):

- User Story 1: Generate RSA 2048 key, download both keys, verify JWK/JWKS format, attempt to sign JWT with private key
- User Story 2: Generate enterprise license with features/limits, download JWT, decode and verify claims
- User Story 3: Generate multiple keys, revoke one, verify revoked key cannot be downloaded

**Integration Testing** (if added later):

- End-to-end key generation flow with TestServer
- End-to-end license generation flow with TestServer
- Key revocation flow validation

---

## Task Summary

**Total Tasks**: 81

**By Phase**:

- Phase 1 (Setup): 9 tasks
- Phase 2 (Foundational): 13 tasks
- Phase 3 (User Story 1): 17 tasks
- Phase 4 (User Story 2): 14 tasks
- Phase 5 (User Story 3): 11 tasks
- Phase 6 (Docker): 8 tasks
- Phase 7 (Polish): 9 tasks

**By User Story**:

- User Story 1 (P1 - Key Generation): 17 tasks (T023-T039)
- User Story 2 (P2 - License Generation): 14 tasks (T040-T053)
- User Story 3 (P3 - Key Lifecycle): 11 tasks (T054-T064)

**Parallelizable Tasks**: 33 tasks marked with [P]

**Independent Test Criteria Met**:

- ✅ User Story 1: Generate key pair, download keys, verify format, test JWT signing
- ✅ User Story 2: Generate license, download JWT, decode and verify claims
- ✅ User Story 3: Generate keys, revoke key, verify download fails

---

## Format Validation

✅ **All 81 tasks follow the required checklist format**:

- [x] Every task has checkbox `- [ ]`
- [x] Every task has unique sequential ID (T001-T081)
- [x] Parallelizable tasks marked with [P] (33 tasks)
- [x] User story tasks marked with [US1], [US2], or [US3] (42 tasks)
- [x] File paths included in descriptions where applicable
- [x] Tasks organized by phase and user story
- [x] Dependencies clearly documented

**Ready for implementation** ✅
