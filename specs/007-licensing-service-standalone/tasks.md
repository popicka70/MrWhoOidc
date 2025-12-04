# Tasks: Standalone Licensing Service

**Feature**: 007-licensing-service-standalone  
**Generated**: 2025-12-04  
**Status**: Ready for Development

---

## Phase 0: Project Setup

### Task 0.1 – Create Solution Structure

**User Story**: N/A (Infrastructure)  
**Estimate**: 30 min

Create the subfolder structure that mirrors the eventual standalone repository:

```
LicensingService/
├── src/
│   ├── LicensingService.Core/       # Domain logic, entities, services
│   └── LicensingService.Web/        # HTTP surface, minimal APIs, Razor Pages
├── tests/
│   └── LicensingService.Tests/      # Unit and integration tests
└── LicensingService.slnx            # Standalone solution file
```

**Steps**:
1. Create folder structure under `LicensingService/`
2. Create `LicensingService.Core.csproj` targeting .NET 9 with EF Core, JWT dependencies
3. Create `LicensingService.Web.csproj` targeting .NET 9, referencing Core
4. Create `LicensingService.Tests.csproj` with MSTest
5. Create `LicensingService.slnx` including all projects
6. Add reference from MrWhoOidc.slnx to new solution (optional for IDE convenience)

**Acceptance**:
- [X] `dotnet build LicensingService/LicensingService.slnx` succeeds
- [X] Projects follow MrWhoOidc convention (no OpenIddict/Microsoft Identity dependencies)

**Files**:
- `LicensingService/LicensingService.slnx`
- `LicensingService/src/LicensingService.Core/LicensingService.Core.csproj`
- `LicensingService/src/LicensingService.Web/LicensingService.Web.csproj`
- `LicensingService/tests/LicensingService.Tests/LicensingService.Tests.csproj`

---

### Task 0.2 – Configure SQLite/PostgreSQL Database Provider

**User Story**: N/A (Infrastructure)  
**Estimate**: 20 min

Set up EF Core with dual-database support:
- SQLite for development (default)
- PostgreSQL for production (via environment/Aspire)

**Steps**:
1. Add `Npgsql.EntityFrameworkCore.PostgreSQL` and `Microsoft.EntityFrameworkCore.Sqlite` to Core
2. Create `LicensingDbContext` with placeholder entity
3. Configure `Program.cs` to select provider based on configuration
4. Add `appsettings.Development.json` with SQLite connection string

**Acceptance**:
- [X] Application starts with SQLite in dev mode
- [X] Can switch to PostgreSQL via connection string

**Files**:
- `LicensingService/src/LicensingService.Core/Persistence/LicensingDbContext.cs`
- `LicensingService/src/LicensingService.Web/Program.cs`
- `LicensingService/src/LicensingService.Web/appsettings.json`
- `LicensingService/src/LicensingService.Web/appsettings.Development.json`

---

### Task 0.3 – Configure OIDC Authentication

**User Story**: FR-009a (All operations require OIDC authentication)  
**Estimate**: 30 min

Set up JWT Bearer authentication for all API endpoints.

**Steps**:
1. Add `Microsoft.AspNetCore.Authentication.JwtBearer` package to Web
2. Configure JWT Bearer authentication in `Program.cs`
3. Add OIDC authority and audience settings to configuration
4. Apply `[Authorize]` globally via minimal API filter or policy
5. Create `CurrentUserAccessor` service to extract user claims

**Acceptance**:
- [X] Unauthenticated requests receive 401
- [X] Authenticated requests have user claims available
- [X] OIDC settings configurable via appsettings

**Files**:
- `LicensingService/src/LicensingService.Web/Program.cs`
- `LicensingService/src/LicensingService.Web/Services/CurrentUserAccessor.cs`

---

## Phase 1: Core Entities and Persistence (US3 Foundation)

### Task 1.1 – Create Customer Entity

**User Story**: US3, FR-001a  
**Estimate**: 20 min

Implement the Customer entity per data-model.md.

**Steps**:
1. Create `Customer.cs` with all fields from data model
2. Add fluent configuration for indexes and constraints
3. Implement identifier validation (alphanumeric with hyphens)

**Acceptance**:
- [X] Entity matches data-model.md specification
- [X] Unique index on Identifier
- [X] Status defaults to "Active"

**Files**:
- `LicensingService/src/LicensingService.Core/Entities/Customer.cs`
- `LicensingService/src/LicensingService.Core/Persistence/Configurations/CustomerConfiguration.cs`

---

### Task 1.2 – Create LicensedProduct Entity

**User Story**: US2, FR-001  
**Estimate**: 20 min

Implement the LicensedProduct entity.

**Steps**:
1. Create `LicensedProduct.cs` with all fields
2. Add fluent configuration
3. Add navigation property to OptionDefinitions

**Acceptance**:
- [X] Entity matches data-model.md specification
- [X] Unique index on Identifier
- [X] Navigation to ProductOptionDefinition collection

**Files**:
- `LicensingService/src/LicensingService.Core/Entities/LicensedProduct.cs`
- `LicensingService/src/LicensingService.Core/Persistence/Configurations/LicensedProductConfiguration.cs`

---

### Task 1.3 – Create ProductOptionDefinition Entity

**User Story**: US2, FR-002  
**Estimate**: 20 min

Implement the ProductOptionDefinition entity for product-specific licensable options.

**Steps**:
1. Create `ProductOptionDefinition.cs`
2. Add composite unique index on (ProductId, OptionKey)
3. Add DataType enum: String, Number, Boolean

**Acceptance**:
- [X] Entity matches data-model.md specification
- [X] OptionKey validation (lowercase alphanumeric with underscores)
- [X] FK relationship to LicensedProduct

**Files**:
- `LicensingService/src/LicensingService.Core/Entities/ProductOptionDefinition.cs`
- `LicensingService/src/LicensingService.Core/Entities/OptionDataType.cs`
- `LicensingService/src/LicensingService.Core/Persistence/Configurations/ProductOptionDefinitionConfiguration.cs`

---

### Task 1.4 – Create License Entity

**User Story**: US1, US3, FR-005  
**Estimate**: 30 min

Implement the License entity with full lifecycle tracking.

**Steps**:
1. Create `License.cs` with all fields including JSON Options
2. Create `LicenseStatus` enum: Active, Expired, Revoked, Renewed, Upgraded, Downgraded
3. Add self-referencing FK for ParentLicenseId
4. Configure composite indexes for customer-first search

**Acceptance**:
- [X] Entity matches data-model.md specification
- [X] Composite index on (CustomerId, ProductId)
- [X] Composite index on (CustomerId, Status, ValidUntil)
- [X] Self-referencing navigation properties (Parent/Children)

**Files**:
- `LicensingService/src/LicensingService.Core/Entities/License.cs`
- `LicensingService/src/LicensingService.Core/Entities/LicenseStatus.cs`
- `LicensingService/src/LicensingService.Core/Persistence/Configurations/LicenseConfiguration.cs`

---

### Task 1.5 – Create LicenseEvent Entity

**User Story**: US3, FR-011  
**Estimate**: 20 min

Implement the audit trail entity.

**Steps**:
1. Create `LicenseEvent.cs` with JSON Details field
2. Create `LicenseEventType` enum: Created, Renewed, Revoked, Upgraded, Downgraded, Validated
3. Configure immutability (append-only pattern)

**Acceptance**:
- [X] Entity matches data-model.md specification
- [X] Composite index on (LicenseId, Timestamp)
- [X] FK relationship to License

**Files**:
- `LicensingService/src/LicensingService.Core/Entities/LicenseEvent.cs`
- `LicensingService/src/LicensingService.Core/Entities/LicenseEventType.cs`
- `LicensingService/src/LicensingService.Core/Persistence/Configurations/LicenseEventConfiguration.cs`

---

### Task 1.6 – Create SigningKey Entity

**User Story**: US1, FR-015, FR-016  
**Estimate**: 20 min

Implement the signing key entity for JWKS management.

**Steps**:
1. Create `SigningKey.cs` with PublicKeyJwks field
2. Create `SigningKeyStatus` enum: Active, Rotated, Retired
3. Add unique index on Kid

**Acceptance**:
- [X] Entity matches data-model.md specification
- [X] Only public key stored in database
- [X] Unique index on Kid

**Files**:
- `LicensingService/src/LicensingService.Core/Entities/SigningKey.cs`
- `LicensingService/src/LicensingService.Core/Entities/SigningKeyStatus.cs`
- `LicensingService/src/LicensingService.Core/Persistence/Configurations/SigningKeyConfiguration.cs`

---

### Task 1.7 – Complete DbContext and Create Initial Migration

**User Story**: US3  
**Estimate**: 30 min

Finalize LicensingDbContext and create initial migration.

**Steps**:
1. Add all DbSet properties to LicensingDbContext
2. Override OnModelCreating to apply configurations
3. Add UUIDv7 generation using `GuidHelper.NewId()` pattern
4. Create initial migration with all tables

**Commands**:
```bash
dotnet ef migrations add InitialCreate --project LicensingService/src/LicensingService.Core --startup-project LicensingService/src/LicensingService.Web --output-dir Persistence/Migrations
```

**Acceptance**:
- [X] All entities configured in DbContext
- [X] Migration creates all tables with indexes
- [X] `dotnet ef database update` succeeds

**Files**:
- `LicensingService/src/LicensingService.Core/Persistence/LicensingDbContext.cs`
- `LicensingService/src/LicensingService.Core/Persistence/Migrations/*`

---

## Phase 2: US2 – Register and Configure Applications (P1)

### Task 2.1 – Create Product Repository/Store

**User Story**: US2  
**Estimate**: 30 min

Create data access layer for LicensedProduct.

**Steps**:
1. Create `IProductStore` interface with CRUD methods
2. Implement `ProductStore` using EF Core
3. Include soft-delete logic (set Status to Inactive)
4. Include prevention of delete with existing licenses

**Acceptance**:
- [X] CRUD operations work correctly
- [X] Cannot delete product with licenses
- [X] Soft-delete sets Status to Inactive

**Files**:
- `LicensingService/src/LicensingService.Core/Stores/IProductStore.cs`
- `LicensingService/src/LicensingService.Core/Stores/ProductStore.cs`

---

### Task 2.2 – Create Product API Endpoints

**User Story**: US2  
**Estimate**: 45 min

Implement REST endpoints for product management per OpenAPI spec.

**Steps**:
1. Create `ProductEndpoints.cs` with minimal API routes
2. Implement GET /products, POST /products, GET /products/{id}, PUT /products/{id}, DELETE /products/{id}
3. Create request/response DTOs matching OpenAPI schemas
4. Add validation using FluentValidation or data annotations

**Acceptance**:
- [X] All endpoints per openapi.yaml /products section
- [X] 201 on create with Location header
- [X] 409 on duplicate identifier
- [X] 400 on delete with active licenses

**Files**:
- `LicensingService/src/LicensingService.Web/Api/ProductEndpoints.cs`
- `LicensingService/src/LicensingService.Web/Models/ProductModels.cs`

---

### Task 2.3 – Create Product Option Definition Endpoints

**User Story**: US2, FR-002  
**Estimate**: 30 min

Implement endpoints for managing product option definitions.

**Steps**:
1. Add routes to ProductEndpoints for /products/{id}/options
2. POST to add option definition
3. PUT to update option definition
4. DELETE to remove (fails if in use)

**Acceptance**:
- [X] Can add/update/remove option definitions
- [X] Cannot remove option in use by licenses
- [X] Option key uniqueness enforced within product

**Files**:
- `LicensingService/src/LicensingService.Web/Api/ProductEndpoints.cs` (extend)
- `LicensingService/src/LicensingService.Web/Models/OptionDefinitionModels.cs`

---

### Task 2.4 – Create Product Unit Tests

**User Story**: US2  
**Estimate**: 30 min

Write unit tests for product management.

**Steps**:
1. Test ProductStore CRUD operations
2. Test soft-delete prevention with licenses
3. Test option definition management
4. Test API endpoint responses

**Acceptance**:
- [ ] >80% coverage on ProductStore
- [ ] Integration tests for API endpoints

**Files**:
- `LicensingService/tests/LicensingService.Tests/Stores/ProductStoreTests.cs`
- `LicensingService/tests/LicensingService.Tests/Api/ProductEndpointsTests.cs`

---

## Phase 3: Customer Management (US3 Support)

### Task 3.1 – Create Customer Repository/Store

**User Story**: US3, FR-001a  
**Estimate**: 30 min

Create data access layer for Customer.

**Steps**:
1. Create `ICustomerStore` interface
2. Implement `CustomerStore` with CRUD
3. Include search by name/identifier (prefix match)
4. Soft-delete with license check

**Acceptance**:
- [X] CRUD operations work
- [X] Search supports prefix matching
- [X] Cannot delete customer with licenses

**Files**:
- `LicensingService/src/LicensingService.Core/Stores/ICustomerStore.cs`
- `LicensingService/src/LicensingService.Core/Stores/CustomerStore.cs`

---

### Task 3.2 – Create Customer API Endpoints

**User Story**: US3, FR-001a  
**Estimate**: 45 min

Implement REST endpoints for customer management.

**Steps**:
1. Create `CustomerEndpoints.cs`
2. Implement all /customers endpoints per OpenAPI
3. Include /customers/{id}/licenses endpoint for customer-first search
4. Add pagination support

**Acceptance**:
- [X] All endpoints per openapi.yaml /customers section
- [X] Customer-first license lookup works
- [X] Pagination returns correct totals

**Files**:
- `LicensingService/src/LicensingService.Web/Api/CustomerEndpoints.cs`
- `LicensingService/src/LicensingService.Web/Models/CustomerModels.cs`

---

### Task 3.3 – Create Customer Unit Tests

**User Story**: US3  
**Estimate**: 25 min

Write unit tests for customer management.

**Steps**:
1. Test CustomerStore operations
2. Test search functionality
3. Test API endpoints

**Acceptance**:
- [ ] >80% coverage on CustomerStore
- [ ] Integration tests for API endpoints

**Files**:
- `LicensingService/tests/LicensingService.Tests/Stores/CustomerStoreTests.cs`
- `LicensingService/tests/LicensingService.Tests/Api/CustomerEndpointsTests.cs`

---

## Phase 4: US1 – Issue License for Any Application (P1)

### Task 4.1 – Port Signing Key Infrastructure from KeyGen

**User Story**: US1, FR-004  
**Estimate**: 45 min

Reuse ECDSA P-256 signing from MrWhoOidc.KeyGen.

**Steps**:
1. Copy/adapt key generation logic from KeyGen
2. Create `ISigningKeyService` interface
3. Implement key loading from file/secret manager
4. Support multiple active keys for rotation
5. Store public keys in database for JWKS

**Acceptance**:
- [X] Can load existing KeyGen keys
- [X] Can generate new keys
- [X] Public key stored in SigningKey table

**Files**:
- `LicensingService/src/LicensingService.Core/Crypto/ISigningKeyService.cs`
- `LicensingService/src/LicensingService.Core/Crypto/SigningKeyService.cs`
- `LicensingService/src/LicensingService.Core/Crypto/EcdsaKeyHelper.cs`

---

### Task 4.2 – Create License Token Generator Service

**User Story**: US1, FR-004  
**Estimate**: 45 min

Implement JWT license token generation.

**Steps**:
1. Create `ILicenseTokenGenerator` interface
2. Implement using System.IdentityModel.Tokens.Jwt
3. Include claims: jti, iss, sub (customer), aud (product), nbf, exp, tier, scope, options
4. Sign with ES256 algorithm
5. Include kid in header

**Acceptance**:
- [X] Generates valid JWT with all required claims
- [X] Signature verifiable with public key
- [X] Options serialized as JSON claim

**Files**:
- `LicensingService/src/LicensingService.Core/Services/ILicenseTokenGenerator.cs`
- `LicensingService/src/LicensingService.Core/Services/LicenseTokenGenerator.cs`

---

### Task 4.3 – Create License Store

**User Story**: US1, US3, FR-005  
**Estimate**: 40 min

Create data access layer for License entity.

**Steps**:
1. Create `ILicenseStore` interface
2. Implement with customer-first search support
3. Include filtering by product, status, expiry
4. Include parent/child relationship handling

**Acceptance**:
- [X] CRUD operations work
- [X] Customer-first search with filters
- [X] Parent/child relationships maintained

**Files**:
- `LicensingService/src/LicensingService.Core/Stores/ILicenseStore.cs`
- `LicensingService/src/LicensingService.Core/Stores/LicenseStore.cs`

---

### Task 4.4 – Create License Issuance Service

**User Story**: US1, FR-004, FR-005  
**Estimate**: 45 min

Implement the core license issuance logic.

**Steps**:
1. Create `ILicenseService` interface
2. Implement `IssueLicense` method
3. Validate options against product option definitions
4. Generate token and persist license
5. Create LicenseEvent for audit trail

**Acceptance**:
- [ ] License issued with valid token
- [ ] Options validated against product catalog
- [ ] Audit event created
- [ ] Returns token and license metadata

**Files**:
- `LicensingService/src/LicensingService.Core/Services/ILicenseService.cs`
- `LicensingService/src/LicensingService.Core/Services/LicenseService.cs`

---

### Task 4.5 – Create License API Endpoints (Issue)

**User Story**: US1  
**Estimate**: 40 min

Implement POST /licenses endpoint.

**Steps**:
1. Create `LicenseEndpoints.cs`
2. Implement POST /licenses
3. Implement GET /licenses with filters
4. Implement GET /licenses/{id}
5. Return token in response for new licenses

**Acceptance**:
- [X] POST creates license and returns token
- [X] GET supports all filter parameters
- [X] 400 on invalid options

**Files**:
- `LicensingService/src/LicensingService.Web/Api/LicenseEndpoints.cs`
- `LicensingService/src/LicensingService.Web/Models/LicenseModels.cs`

---

### Task 4.6 – Create License Issuance Tests

**User Story**: US1  
**Estimate**: 40 min

Write comprehensive tests for license issuance.

**Steps**:
1. Test token generation with valid claims
2. Test option validation
3. Test audit event creation
4. Test API endpoint

**Acceptance**:
- [X] Token claims verified
- [X] Invalid options rejected
- [X] Audit trail created
- [X] >80% coverage

**Files**:
- `LicensingService/tests/LicensingService.Tests/Services/LicenseTokenGeneratorTests.cs`
- `LicensingService/tests/LicensingService.Tests/Stores/LicenseStoreTests.cs`

---

## Phase 5: US4 – Renew Existing Licenses (P2)

### Task 5.1 – Implement License Renewal Logic

**User Story**: US4, FR-007, FR-007a  
**Estimate**: 45 min

Implement renewal with 60-day overlap.

**Steps**:
1. Add `RenewLicense` method to LicenseService
2. Create new license starting from original expiry date
3. Set original license status to "Renewed"
4. Link via ParentLicenseId
5. Maintain 60-day overlap period

**Acceptance**:
- [ ] New license created with correct validity period
- [ ] Original marked as "Renewed"
- [ ] Both licenses valid during overlap
- [ ] Audit events for both licenses

**Files**:
- `LicensingService/src/LicensingService.Core/Services/LicenseService.cs` (extend)

---

### Task 5.2 – Create Renewal API Endpoint

**User Story**: US4  
**Estimate**: 25 min

Implement POST /licenses/{id}/renew endpoint.

**Steps**:
1. Add route to LicenseEndpoints
2. Accept renewal duration and optional option updates
3. Return new license with token

**Acceptance**:
- [ ] Renewal creates new license
- [ ] 400 if license is revoked
- [ ] Returns new token

**Files**:
- `LicensingService/src/LicensingService.Web/Api/LicenseEndpoints.cs` (extend)
- `LicensingService/src/LicensingService.Web/Models/RenewalModels.cs`

---

### Task 5.3 – Create Renewal Tests

**User Story**: US4  
**Estimate**: 30 min

Test renewal functionality.

**Steps**:
1. Test 60-day overlap calculation
2. Test parent/child linking
3. Test status transitions
4. Test option modification during renewal

**Acceptance**:
- [ ] Overlap period correct
- [ ] Status transitions correct
- [ ] Cannot renew revoked license

**Files**:
- `LicensingService/tests/LicensingService.Tests/Services/LicenseRenewalTests.cs`

---

## Phase 6: US5 – Revoke Licenses (P2)

### Task 6.1 – Implement License Revocation Logic

**User Story**: US5, FR-008  
**Estimate**: 30 min

Implement revocation with reason tracking.

**Steps**:
1. Add `RevokeLicense` method to LicenseService
2. Set status to "Revoked"
3. Record RevokedAt, RevokedBy, RevocationReason
4. Create audit event

**Acceptance**:
- [ ] License status set to Revoked
- [ ] Revocation metadata recorded
- [ ] Audit event created

**Files**:
- `LicensingService/src/LicensingService.Core/Services/LicenseService.cs` (extend)

---

### Task 6.2 – Create Revocation API Endpoint

**User Story**: US5  
**Estimate**: 20 min

Implement POST /licenses/{id}/revoke endpoint.

**Steps**:
1. Add route to LicenseEndpoints
2. Require reason in request body
3. Return updated license

**Acceptance**:
- [ ] Revocation succeeds with reason
- [ ] 400 if already revoked
- [ ] Reason stored

**Files**:
- `LicensingService/src/LicensingService.Web/Api/LicenseEndpoints.cs` (extend)
- `LicensingService/src/LicensingService.Web/Models/RevocationModels.cs`

---

### Task 6.3 – Create Revocation Tests

**User Story**: US5  
**Estimate**: 20 min

Test revocation functionality.

**Acceptance**:
- [ ] Status transition correct
- [ ] Cannot revoke twice
- [ ] Reason required

**Files**:
- `LicensingService/tests/LicensingService.Tests/Services/LicenseRevocationTests.cs`

---

## Phase 7: US6 – License Validation Endpoint (P2)

### Task 7.1 – Implement License Validation Service

**User Story**: US6, FR-009  
**Estimate**: 40 min

Implement license validation logic.

**Steps**:
1. Add `ValidateLicense` method to LicenseService
2. Verify token signature
3. Check license status in database (not revoked)
4. Check expiry
5. Return validation result with metadata

**Acceptance**:
- [ ] Valid license returns success with metadata
- [ ] Revoked license returns invalid
- [ ] Expired license returns invalid
- [ ] Tampered token returns invalid

**Files**:
- `LicensingService/src/LicensingService.Core/Services/LicenseService.cs` (extend)
- `LicensingService/src/LicensingService.Core/Services/LicenseValidationResult.cs`

---

### Task 7.2 – Create Validation API Endpoint

**User Story**: US6  
**Estimate**: 25 min

Implement POST /licenses/validate endpoint.

**Steps**:
1. Add route to LicenseEndpoints
2. Accept token in request body
3. Return validation result

**Acceptance**:
- [ ] Validation endpoint responds correctly
- [ ] Response includes license metadata on success
- [ ] Response includes failure reason on invalid

**Files**:
- `LicensingService/src/LicensingService.Web/Api/LicenseEndpoints.cs` (extend)
- `LicensingService/src/LicensingService.Web/Models/ValidationModels.cs`

---

### Task 7.3 – Create Validation Tests

**User Story**: US6  
**Estimate**: 30 min

Test validation scenarios.

**Steps**:
1. Test valid license validation
2. Test revoked license rejection
3. Test expired license rejection
4. Test signature verification failure

**Acceptance**:
- [ ] All validation scenarios covered
- [ ] <200ms response time verified

**Files**:
- `LicensingService/tests/LicensingService.Tests/Services/LicenseValidationTests.cs`

---

## Phase 8: JWKS Endpoint (FR-015)

### Task 8.1 – Implement JWKS Endpoint

**User Story**: FR-015, FR-016  
**Estimate**: 30 min

Expose public keys for offline license verification.

**Steps**:
1. Create `JwksEndpoints.cs`
2. Implement GET /.well-known/jwks.json
3. Return all active/rotated signing key public keys
4. No authentication required

**Acceptance**:
- [ ] JWKS endpoint returns valid JWK set
- [ ] All active keys included
- [ ] Rotated keys included for verification of existing licenses

**Files**:
- `LicensingService/src/LicensingService.Web/Api/JwksEndpoints.cs`

---

## Phase 9: US7 – Upgrade/Downgrade License Tier (P3)

### Task 9.1 – Implement Tier Change Logic

**User Story**: US7, FR-010  
**Estimate**: 40 min

Implement license tier upgrade/downgrade.

**Steps**:
1. Add `UpgradeLicense` and `DowngradeLicense` methods
2. Create new license with new tier
3. Set original status to "Upgraded" or "Downgraded"
4. Handle feature inheritance/removal

**Acceptance**:
- [ ] New license created with correct tier
- [ ] Original marked appropriately
- [ ] Features adjusted for tier

**Files**:
- `LicensingService/src/LicensingService.Core/Services/LicenseService.cs` (extend)

---

### Task 9.2 – Create Tier Change API Endpoints

**User Story**: US7  
**Estimate**: 25 min

Implement POST /licenses/{id}/upgrade and /downgrade endpoints.

**Acceptance**:
- [ ] Upgrade creates new license with higher tier
- [ ] Downgrade creates new license with lower tier
- [ ] Returns new token

**Files**:
- `LicensingService/src/LicensingService.Web/Api/LicenseEndpoints.cs` (extend)
- `LicensingService/src/LicensingService.Web/Models/TierChangeModels.cs`

---

### Task 9.3 – Create Tier Change Tests

**User Story**: US7  
**Estimate**: 25 min

Test tier change functionality.

**Acceptance**:
- [ ] Upgrade flow correct
- [ ] Downgrade flow correct
- [ ] Feature adjustment verified

**Files**:
- `LicensingService/tests/LicensingService.Tests/Services/LicenseTierChangeTests.cs`

---

## Phase 10: US8 – Bulk License Operations (P3)

### Task 10.1 – Implement Bulk Operations

**User Story**: US8, FR-012  
**Estimate**: 45 min

Implement bulk renewal and revocation.

**Steps**:
1. Add `BulkRenewLicenses` method
2. Add `BulkRevokeLicenses` method
3. Use transactions for atomicity
4. Return summary of results

**Acceptance**:
- [ ] Bulk renewal processes all selected licenses
- [ ] Bulk revocation processes all selected licenses
- [ ] Partial failure handling (report which failed)

**Files**:
- `LicensingService/src/LicensingService.Core/Services/LicenseService.cs` (extend)
- `LicensingService/src/LicensingService.Core/Services/BulkOperationResult.cs`

---

### Task 10.2 – Create Bulk Operation API Endpoints

**User Story**: US8  
**Estimate**: 25 min

Implement bulk endpoints.

**Acceptance**:
- [ ] POST /licenses/bulk-renew works
- [ ] POST /licenses/bulk-revoke works
- [ ] Returns operation summary

**Files**:
- `LicensingService/src/LicensingService.Web/Api/LicenseEndpoints.cs` (extend)
- `LicensingService/src/LicensingService.Web/Models/BulkOperationModels.cs`

---

### Task 10.3 – Create Bulk Operation Tests

**User Story**: US8  
**Estimate**: 25 min

Test bulk operations.

**Acceptance**:
- [ ] 100 licenses processed in <10 seconds (SC-005)
- [ ] Partial failure handling correct

**Files**:
- `LicensingService/tests/LicensingService.Tests/Services/BulkOperationTests.cs`

---

## Phase 11: Admin UI (Razor Pages)

### Task 11.1 – Create Admin Layout and Navigation

**User Story**: All  
**Estimate**: 30 min

Create Razor Pages layout for admin interface.

**Steps**:
1. Create `_Layout.cshtml` with navigation
2. Create `_ViewImports.cshtml` and `_ViewStart.cshtml`
3. Add navigation links: Customers, Products, Licenses

**Files**:
- `LicensingService/src/LicensingService.Web/Pages/Shared/_Layout.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/_ViewImports.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/_ViewStart.cshtml`

---

### Task 11.2 – Create Customer Admin Pages

**User Story**: FR-001a  
**Estimate**: 45 min

Create CRUD pages for customers.

**Steps**:
1. Create Index, Create, Edit, Details pages
2. Include customer search functionality
3. Show license count per customer

**Files**:
- `LicensingService/src/LicensingService.Web/Pages/Customers/Index.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/Customers/Create.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/Customers/Edit.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/Customers/Details.cshtml`

---

### Task 11.3 – Create Product Admin Pages

**User Story**: US2  
**Estimate**: 45 min

Create CRUD pages for products with option management.

**Steps**:
1. Create Index, Create, Edit pages
2. Include option definition management on Edit page

**Files**:
- `LicensingService/src/LicensingService.Web/Pages/Products/Index.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/Products/Create.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/Products/Edit.cshtml`

---

### Task 11.4 – Create License Admin Pages

**User Story**: US1, US3, US4, US5  
**Estimate**: 60 min

Create license management pages.

**Steps**:
1. Create Index with customer-first search
2. Create Issue License page (select customer, product, tier, options)
3. Create Details page with renewal/revocation actions
4. Show audit trail on Details page

**Files**:
- `LicensingService/src/LicensingService.Web/Pages/Licenses/Index.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/Licenses/Issue.cshtml`
- `LicensingService/src/LicensingService.Web/Pages/Licenses/Details.cshtml`

---

## Phase 12: Polish and Cross-Cutting

### Task 12.1 – Add Health Check Endpoint

**User Story**: N/A (Operations)  
**Estimate**: 15 min

Add health check for deployment verification.

**Steps**:
1. Add `AspNetCore.HealthChecks.SqlServer` and SQLite equivalent
2. Configure /health endpoint
3. Include database connectivity check

**Files**:
- `LicensingService/src/LicensingService.Web/Program.cs` (extend)

---

### Task 12.2 – Add OpenAPI Documentation

**User Story**: N/A (Developer Experience)  
**Estimate**: 20 min

Configure Swagger/OpenAPI.

**Steps**:
1. Add Swashbuckle packages
2. Configure to match contracts/openapi.yaml
3. Add JWT Bearer authentication in Swagger UI

**Files**:
- `LicensingService/src/LicensingService.Web/Program.cs` (extend)

---

### Task 12.3 – Verify KeyGen Compatibility

**User Story**: SC-007  
**Estimate**: 30 min

Ensure existing KeyGen functionality remains intact.

**Steps**:
1. Run existing MrWhoOidc.KeyGen tests
2. Verify KeyGen can still generate standalone licenses
3. Document any shared key format requirements

**Acceptance**:
- [ ] All KeyGen tests pass
- [ ] No regression in existing functionality

---

### Task 12.4 – Create Integration Test Suite

**User Story**: All  
**Estimate**: 45 min

Create end-to-end integration tests.

**Steps**:
1. Create WebApplicationFactory-based test setup
2. Test full license lifecycle: create product → create customer → issue license → renew → revoke
3. Test validation endpoint flow

**Acceptance**:
- [ ] Full lifecycle test passes
- [ ] Validation endpoint test passes

**Files**:
- `LicensingService/tests/LicensingService.Tests/Integration/LicenseLifecycleTests.cs`

---

### Task 12.5 – Final Documentation

**User Story**: N/A  
**Estimate**: 30 min

Create README and deployment documentation.

**Steps**:
1. Create `LicensingService/README.md`
2. Document configuration options
3. Document deployment steps
4. Update quickstart.md with any changes

**Files**:
- `LicensingService/README.md`

---

## Summary

| Phase | Tasks | Est. Hours |
|-------|-------|------------|
| 0: Project Setup | 3 | 1.3 |
| 1: Core Entities | 7 | 2.7 |
| 2: Products (US2) | 4 | 2.3 |
| 3: Customers | 3 | 1.7 |
| 4: Issue License (US1) | 6 | 4.3 |
| 5: Renewal (US4) | 3 | 1.7 |
| 6: Revocation (US5) | 3 | 1.2 |
| 7: Validation (US6) | 3 | 1.6 |
| 8: JWKS | 1 | 0.5 |
| 9: Tier Change (US7) | 3 | 1.5 |
| 10: Bulk Ops (US8) | 3 | 1.6 |
| 11: Admin UI | 4 | 3.0 |
| 12: Polish | 5 | 2.3 |
| **Total** | **48** | **~26 hours** |

---

## Dependencies

```text
Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 4 → [Phases 5-10 parallel]
                                         ↓
                                    Phase 8 (JWKS)
                                         ↓
                               Phase 11 (Admin UI)
                                         ↓
                                Phase 12 (Polish)
```

P1 stories (US1-3) complete by end of Phase 4.  
P2 stories (US4-6) complete by end of Phase 7.  
P3 stories (US7-8) complete by end of Phase 10.
