# Research: Standalone Licensing Service

**Feature**: 007-licensing-service-standalone  
**Date**: 2025-12-04  
**Status**: Complete

## Research Tasks

### 1. OIDC Authentication Integration Pattern

**Question**: How should the licensing service integrate OIDC authentication for all operations?

**Decision**: Use ASP.NET Core OIDC middleware with JWT Bearer authentication

**Rationale**:
- Standard ASP.NET Core pattern (`AddAuthentication().AddJwtBearer()`)
- Compatible with any OIDC provider (including MrWhoOidc.WebAuth itself)
- Supports both interactive (Razor Pages) and API (Bearer token) flows
- Well-documented, production-proven approach

**Alternatives Considered**:
- Custom OIDC validation: Too complex, reinventing the wheel
- API key only: Doesn't meet OIDC requirement from clarification
- Cookie-only: Doesn't work for API consumers

**Implementation Notes**:
- Configure OIDC authority in appsettings (configurable per environment)
- Use `[Authorize]` attribute on all endpoints
- Admin UI uses OIDC code flow; APIs use Bearer tokens
- Consider adding role claims for admin vs read-only access

---

### 2. License Token Signing Key Management

**Question**: How to handle signing keys for multi-product licensing?

**Decision**: Reuse existing KeyGen cryptography with product-specific key IDs

**Rationale**:
- KeyGen already has proven ECDSA P-256 signing implementation
- JWKS endpoint already implemented for key distribution
- Key rotation logic exists and is tested
- Single signing key per service instance (not per product) simplifies management

**Alternatives Considered**:
- Per-product signing keys: Overcomplicated key management; tokens already contain product ID
- RSA instead of ECDSA: Larger tokens, no benefit for this use case
- External KMS (Azure Key Vault, AWS KMS): Good for production but adds dependency; defer to deployment config

**Implementation Notes**:
- Copy `LicenseGenerationService` signing logic to new Core project
- Signing key configured via appsettings (same pattern as current KeyGen)
- Include `kid` in JWT header for key rotation support
- JWKS endpoint serves public key for offline validation

---

### 3. License Overlap Implementation Strategy

**Question**: How to implement the 60-day overlap for license renewals?

**Decision**: New license `nbf` (not-before) = now; original license unchanged until natural expiry

**Rationale**:
- Both licenses valid during overlap (per spec requirement)
- No modification to existing license required
- Simple implementation: renewal just creates new license with parent reference
- Applications can use either license during transition period

**Alternatives Considered**:
- Extend original license: Loses audit trail of renewal event
- Invalidate original on renewal: Violates overlap requirement
- Complex state machine: Overcomplicated for the use case

**Implementation Notes**:
- License entity has `ParentLicenseId` nullable FK for renewal chain
- Renewal creates new license: `ValidFrom = now`, `ValidUntil = original.ValidUntil + extension`
- Original license status unchanged (remains "active" until natural expiry)
- Query for "latest active license" returns newest in chain

---

### 4. Customer-First Search Pattern

**Question**: How to efficiently implement customer-first license search?

**Decision**: Indexed composite queries with EF Core

**Rationale**:
- Customer table with proper indexes on name/identifier
- License table indexed on CustomerId + ProductId
- EF Core LINQ queries translate to efficient SQL
- Pagination built-in for large result sets

**Alternatives Considered**:
- Full-text search (PostgreSQL FTS): Overkill for exact/prefix matching
- Elasticsearch: External dependency, not needed at this scale
- Denormalized views: Premature optimization

**Implementation Notes**:
- Index on `License(CustomerId, Status, ValidUntil)` for common queries
- Index on `License(CustomerId, ProductId)` for renewal lookup
- Customer search supports partial name matching (LIKE prefix%)
- API returns paginated results with total count

---

### 5. Product-Specific Options as Key-Value Pairs

**Question**: How to store and validate product-specific license options?

**Decision**: JSON column for options with product catalog validation at service layer

**Rationale**:
- Options stored as JSON object in License entity (`{"max_users": 100, "region": "EU"}`)
- Product defines available options in ProductOptionDefinition table
- Service layer validates options against product catalog before persisting
- Flexible: supports string, number, boolean values per clarification

**Alternatives Considered**:
- EAV (Entity-Attribute-Value) tables: Query complexity, poor performance
- Separate option tables per product: Doesn't scale
- Unvalidated JSON: Risk of invalid data

**Implementation Notes**:
- `License.Options` column: `jsonb` in PostgreSQL, `TEXT` in SQLite
- `ProductOptionDefinition` table: ProductId, OptionKey, DataType, DefaultValue
- Validation service checks option keys exist and values match expected types
- Options embedded in JWT claims for offline consumption

---

### 6. Audit Trail Implementation

**Question**: How to implement comprehensive license lifecycle audit trail?

**Decision**: Event sourcing pattern with LicenseEvent table

**Rationale**:
- Every license operation creates a LicenseEvent record
- Events are append-only (never updated/deleted)
- Full history reconstructable from events
- Simple query for audit display

**Alternatives Considered**:
- Temporal tables (PostgreSQL): Database-specific, harder to query
- Separate audit database: Overcomplicated for this scale
- Logging only: Not queryable for UI display

**Implementation Notes**:
- LicenseEvent: LicenseId, EventType (Created, Renewed, Revoked, Upgraded, Downgraded), Timestamp, Actor, Details (JSON)
- Actor = authenticated user ID from OIDC claims
- Details JSON contains before/after for changes
- Index on LicenseId for efficient history retrieval

---

### 7. Validation Endpoint Design

**Question**: What should the validation endpoint return and how?

**Decision**: POST endpoint accepting JWT, returning structured validation result

**Rationale**:
- POST with token in body (not URL) for security
- Response includes: valid (bool), status, expiry, product, tier, options
- Invalid responses include reason code for debugging
- Authentication required per clarification (OIDC Bearer token)

**Alternatives Considered**:
- GET with token in query: Token visible in logs/URLs
- Introspection endpoint (RFC 7662): More complex, designed for access tokens
- GraphQL: Overkill for single-purpose endpoint

**Implementation Notes**:
- `POST /api/v1/licenses/validate` with `{ "token": "..." }` body
- Response: `{ "valid": true, "license": { ... } }` or `{ "valid": false, "reason": "revoked" }`
- Checks: signature valid → not expired → not revoked → product still active
- Returns 401 if caller not authenticated, 200 with result otherwise

---

### 8. Database Migration from SQLite to PostgreSQL

**Question**: How to support both SQLite (dev) and PostgreSQL (prod)?

**Decision**: EF Core provider switching via configuration

**Rationale**:
- Standard EF Core pattern: connection string determines provider
- Same DbContext, different providers
- Migrations work for both (with minor SQL differences handled by EF)
- Consistent with MrWhoOidc patterns

**Alternatives Considered**:
- PostgreSQL only: Heavier local dev setup
- SQLite only: Not production-ready
- Docker Compose for local PostgreSQL: Valid but optional

**Implementation Notes**:
- appsettings.Development.json: SQLite connection
- appsettings.Production.json: PostgreSQL connection  
- Provider detection in Program.cs: `UseSqlite` vs `UseNpgsql`
- Test against both providers in CI

---

## Summary

All research questions resolved. No blocking unknowns remain.

**Key Decisions**:
1. OIDC via ASP.NET Core middleware (JWT Bearer + OIDC code flow)
2. Reuse KeyGen signing logic with JWKS endpoint
3. Overlap via parallel valid licenses (no original modification)
4. Customer-first search with composite indexes
5. Options as validated JSON with product catalog
6. Append-only LicenseEvent for audit trail
7. POST validation endpoint with structured response
8. EF Core provider switching for SQLite/PostgreSQL
