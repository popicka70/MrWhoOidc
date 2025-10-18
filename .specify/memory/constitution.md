# MrWhoOidc Constitution

<!--
Sync Impact Report (2025-10-18):

- Version: 1.1.0 → 1.1.1 (PATCH: Markdown formatting fixes for proper MdMcp compliance)
- Modified Principles: None (content unchanged)
- Added Sections: None
- Removed Sections: None
- Templates Requiring Updates: None (formatting fixes only)
- Follow-up TODOs: None
- Rationale: Applied MdMcp formatting fixes to resolve MD022, MD032, MD036 violations

-->

<!-- OIDC Identity Provider (IdP) with Multi-Tenancy and IdP Chaining -->

## Core Principles

### I. OIDC Specification Compliance (NON-NEGOTIABLE)

We follow the OpenID Connect specification strictly.

- All protocol implementations must conform to [OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- OAuth 2.0 flows must follow [RFC 6749](https://datatracker.ietf.org/doc/html/rfc6749) and related RFCs
- Security best practices per [OAuth 2.0 Security Best Current Practice](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-security-topics)
- When in doubt, consult the specification before implementation
- Protocol violations are production blockers

### II. Domain-Driven Architecture

Separation of concerns: Domain logic vs HTTP handling.

- **MrWhoOidc.Auth**: Core OIDC domain logic, protocols, persistence, crypto, key management, services
  - EF Core + PostgreSQL via Aspire connection "authdb"
  - No HTTP concerns (no controllers, no middleware, no Razor Pages)
  - Business logic, validation, token generation, persistence
- **MrWhoOidc.WebAuth**: HTTP surface layer
  - Minimal APIs + Razor Pages
  - Discovery, JWKS, authorize, token, userinfo, logout endpoints
  - Admin UI for configuration
  - Consumes MrWhoOidc.Auth services
- Clear boundary: Auth = "what/why", WebAuth = "how/when"

### III. .NET 9 Technology Stack

Modern .NET stack with Aspire orchestration.

- **Framework**: .NET 9 across all projects
- **Database**: PostgreSQL with EF Core migrations
- **Development**: Aspire for local orchestration (MrWhoOidc.AppHost)
- **Deployment**: Docker containers for local testing
- **Testing**: MSTest framework
- **Target**: No OpenIddict or Microsoft Identity Platform packages (custom implementation)

### IV. Integration Test Coverage

We aim to cover the solution with integration tests.

- Protocol flows tested end-to-end (authorize → token → userinfo)
- Multi-tenant scenarios validated
- IdP chaining flows verified
- Database interactions tested against real PostgreSQL
- Use TestServer for in-process HTTP testing
- Tests must validate OIDC specification compliance

### V. Security & Multi-Tenancy

Production-grade security with tenant isolation.

- Role-based authorization: `tenant-admin`, `platform-admin` policies
- CSRF protection via automatic antiforgery token validation
- Tenant isolation enforced at data layer (ITenantAccessor)
- Rate limiting on admin APIs
- Secure credential storage (Argon2id/BCrypt for passwords)
- Key rotation procedures documented (see key-rotation-playbook.md)

### VI. Zero-Warning Policy

We do not accept build warnings as finished work.

- All code must compile with zero warnings in both Debug and Release configurations
- Compiler warnings (`CS*`) must be resolved, not suppressed without justification
- Analyzer warnings (Roslyn, StyleCop, etc.) must be addressed
- Build logs must show clean compilation output
- Exception: Warnings may be temporarily suppressed with:
  - `#pragma warning disable` with inline comment explaining why
  - `.editorconfig` rule disabling with documented rationale
  - Suppressions must be reviewed and tracked in technical debt backlog
- Rationale: Warnings often indicate real issues that become bugs in production. Clean builds ensure code quality and maintainability.

## Architecture & Project Structure

### Solution Layout

```text
MrWhoOidc.Auth/           # Core OIDC domain logic
  ├── Protocols/           # OIDC/OAuth protocol implementations
  ├── Persistence/         # EF Core, migrations, DbContext
  ├── Cryptography/        # Key management, signing, encryption
  ├── Services/            # Domain services (TokenService, ConsentService, etc.)
  └── MultiTenancy/        # Tenant isolation logic

MrWhoOidc.WebAuth/        # HTTP/UI layer (OpenID Provider)
  ├── Handlers/            # Minimal API handlers (discovery, token, authorize, etc.)
  ├── Pages/               # Razor Pages (admin UI, login, consent)
  ├── Background/          # Background workers (BCL dispatcher)
  └── Infrastructure/      # Middleware, endpoint mapping, service registration

MrWhoOidc.ApiService/     # Sample downstream API (DPoP support)
MrWhoOidc.Security/       # Cross-cutting security helpers (DPoP)
MrWhoOidc.ServiceDefaults/ # Logging/OpenTelemetry defaults
MrWhoOidc.AppHost/        # Aspire orchestration host
MrWhoOidc.UnitTests/      # Unit + integration tests
MrWhoOidc.Web/            # Sample RP (Relying Party) client
```

### Database Management

PostgreSQL via Aspire-provided connection.

- Connection name: `authdb`
- Never hardcode connection strings
- Migrations live in `MrWhoOidc.Auth/Persistence/Migrations`

#### Migration Creation (MANDATORY)

- **ALWAYS use EF Core migrations tool** to generate migration files
- **NEVER create migration files from scratch manually**
- Generated files can be edited if necessary (e.g., for custom SQL, index tuning)
- This ensures migrations are properly tracked and DbContext stays in sync

#### Migration Commands

```bash
# Add migration (ALWAYS use this tool)
dotnet ef migrations add <Name> --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations

# Update database
dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth

# Remove last migration (if not applied to database)
dotnet ef migrations remove --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth
```

### Primary Key Strategy (UUIDv7)

Time-ordered UUIDs for optimal database performance.

- **ALWAYS use `GuidHelper.NewId()`** for entity primary keys (never `Guid.NewGuid()`)
- Generates UUIDv7 (RFC 9562) with embedded timestamps
- Located at: `MrWhoOidc.Auth/Persistence/GuidHelper.cs`
- Example: `public Guid Id { get; set; } = GuidHelper.NewId();`

#### Benefits

- 80-90% reduction in B-tree page splits during inserts
- 50%+ lower latency for insert operations on high-volume tables
- Better cache locality due to sequential writes
- Implicit chronological ordering (millisecond precision)
- Fully compatible with existing PostgreSQL `uuid` columns

#### Compatibility

- Existing UUIDv4 records remain valid (no data migration required)
- Foreign keys work transparently with mixed UUID versions
- External APIs unchanged (standard RFC 4122 string serialization)

#### For New Entities

```csharp
public class MyNewEntity
{
    public Guid Id { get; set; } = GuidHelper.NewId();  // ✅ Correct
    // NOT: = Guid.NewGuid();  // ❌ Don't use this
}
```

#### References

- RFC 9562: <https://www.rfc-editor.org/rfc/rfc9562.html>
- Implementation: `MrWhoOidc.Auth/Persistence/GuidHelper.cs`
- Backlog: `docs/uuidv7-migration-backlog.md`

### Key Features & Endpoints

#### Core OIDC Endpoints (in MrWhoOidc.WebAuth)

- Discovery: `/.well-known/openid-configuration`
- JWKS: `/jwks`, `/{providerId}/jwks` (per-provider)
- Authorization: `/authorize`
- Token: `/token`
- UserInfo: `/userinfo`
- Logout: `/logout`, `/logout/callback`
- Back-Channel Logout (BCL): Durable outbox + dispatcher with retries

#### Admin APIs (REST)

- `/admin/api/providers` (CRUD for identity providers)
- `/admin/api/clients` (CRUD for OAuth clients)
- `/admin/api/keys` (key management)
- All secured with `tenant-admin` policy + rate limiting

## Development Workflow

### Building & Running

```bash
# Build entire solution
dotnet build

# Run tests
dotnet test

# Run with Aspire (recommended for development)
dotnet run --project MrWhoOidc.AppHost
```

### Testing Standards

MSTest with TestServer for integration tests.

- Unit tests: Mock external dependencies, test domain logic
- Integration tests: Use TestServer + in-memory DB or test DB
- Test naming: `[Method]_[Scenario]_[ExpectedOutcome]`
- Example: `OnPostAsync_ValidInput_RedirectsToIndex`

#### Test Organization (in MrWhoOidc.UnitTests)

- `*Tests.cs` for unit tests
- `*IntegrationTests.cs` for integration tests
- Use `TestDataSeeder.cs` for test data setup

#### OIDC RFC Documentation (MANDATORY)

- **ALWAYS include RFC references in XML documentation** when tests validate OIDC specification behavior
- Format: `/// <summary>Tests [behavior]. See RFC XXXX Section X.X.</summary>`
- Example: `/// <summary>Validates authorization code flow. See RFC 6749 Section 4.1.</summary>`
- Ensures traceability between test coverage and specification compliance

### Documentation Requirements

Comprehensive documentation in `/docs`.

- Architectural Decision Records (ADRs) in `/docs/adr/`
- Operational playbooks (e.g., key-rotation-playbook.md)
- Security audit reports (RBAC, CSRF, accessibility)
- Developer guide for integration patterns
- Update docs when changing protocols/endpoints

#### Markdown Formatting Standards (MANDATORY)

- **ALWAYS follow Markdown specification** to avoid lint warnings
- Add blank lines before and after headings (MD022)
- Add blank lines before and after lists (MD032)
- Add blank lines before and after code fences (MD031)
- Specify language for code blocks (MD040): ` ```bash `, ` ```csharp `, ` ```text `
- Files must end with single newline character (MD047)
- Use linters (markdownlint) to validate before committing

#### Backlog Management

- Use Markdown checkboxes to track backlog item states
- `[ ]` = Not started / To-do
- `[x]` = Completed / Done
- `[~]` = In progress / Partially complete / Deferred
- Update backlog documents when items change status
- Example:

  ```markdown
  - [ ] Implement refresh token rotation
  - [~] Add device flow support (in progress)
  - [x] Complete BCL implementation
  ```

### Security Practices

Defense in depth across all layers.

- Passwords: Argon2id or BCrypt (never plaintext)
- Protocol validation: Validate all OIDC/OAuth params
- Key management: Strong rotation with overlap, include `kid`
- Backchannel auditing: Structured logs with PII hashing
- CSRF protection: Global `AutoValidateAntiforgeryTokenAttribute`
- RBAC: Group-level authorization on admin endpoints

## Governance & Constraints

### Architectural Constraints

1. **No OpenIddict/Microsoft Identity Platform**: Custom OIDC implementation
2. **Domain logic in Auth project**: No HTTP concerns in Auth layer
3. **PostgreSQL required**: Aspire-provided connection "authdb"
4. **.NET 9 only**: No framework downgrade
5. **Minimal APIs + Razor Pages**: No MVC controllers in WebAuth

### Code Review Requirements

- OIDC specification compliance verified
- Security implications assessed (RBAC, CSRF, tenant isolation)
- Test coverage for protocol flows
- Documentation updated for protocol changes
- Migration scripts reviewed for data safety

### Adding Features

When adding new OIDC features:

1. Place core logic in `MrWhoOidc.Auth` (domain)
2. Expose via `MrWhoOidc.WebAuth` minimal APIs
3. Add/adjust EF Core migrations if schema changes
4. Update `/docs` for protocol/endpoint changes
5. Add unit tests in `MrWhoOidc.UnitTests`
6. Add integration tests for E2E flows

### Prohibited Patterns

- ❌ HTTP logic in MrWhoOidc.Auth (violates domain separation)
- ❌ Hardcoded connection strings (use Aspire-provided)
- ❌ Hand-editing DB schema (always use migrations)
- ❌ Skipping tests for protocol changes
- ❌ Logging raw JWTs or sensitive tokens

## Key Reference Documents

### Essential reading for contributors

- `.github/copilot-instructions.md` – AI assistant guidance
- `docs/key-rotation-playbook.md` – Operational procedures
- `docs/adr/adr-0009-jwks-endpoints.md` – JWKS design decisions
- `docs/developer-guide.md` – Integration patterns
- `docs/admin-api-rbac-audit-2025-10-15.md` – Security baseline
- `docs/p0-production-readiness-summary.md` – Production checklist

### OpenID Connect Specifications

- [OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- [OAuth 2.0 (RFC 6749)](https://datatracker.ietf.org/doc/html/rfc6749)
- [OAuth 2.0 Security Best Practices](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-security-topics)

## Amendments

This constitution supersedes all other practices. Amendments require:

1. Documentation of rationale in ADR
2. Review by platform maintainers
3. Migration plan for existing code
4. Update to this document with version bump

---

**Version**: 1.1.1 | **Ratified**: 2025-10-15 | **Last Amended**: 2025-10-18
