# MrWhoOidc Copilot Instructions

> **Note:** This document has been archived. It contains internal development guidance that may be outdated.

## Purpose
- Guidance for assistants and contributors implementing the OIDC server in this repository.

## Core rules
- Do not add or depend on OpenIddict or Microsoft identity platforms.
- Keep non-visual OIDC logic in `MrWhoOidc.Auth`. Keep UI/pages and HTTP endpoints in `MrWhoOidc.WebAuth`.
- Target .NET 10 for all code.
- Use PostgreSQL via Aspire. Do not hardcode connection strings. Use the Aspire-provided connection named `authdb`.

## EF Core migrations
- Always use the `dotnet ef migrations add ..` command to create migrations.
  - Recommended usage:
    - `dotnet ef migrations add <Name> --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations`
  - Apply migrations when needed:
    - `dotnet ef database update --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth`
  - Keep migration files under `MrWhoOidc.Auth/ Persistence/Migrations`.

## Primary key generation
- **Always use `GuidHelper.NewId()`** for entity primary keys (not `Guid.NewGuid()`).
- `GuidHelper.NewId()` generates UUIDv7 (RFC 9562) with embedded timestamps for optimal database performance.
- Benefits: 80-90% reduction in B-tree page splits, better cache locality, improved index performance.
- Located at: `MrWhoOidc.Auth/Persistence/GuidHelper.cs`
- Example: `public Guid Id { get; set; } = GuidHelper.NewId();`

## Architecture and endpoints
- Implement protocols, persistence, crypto, and key management in `MrWhoOidc.Auth`.
- Implement discovery and JWKS endpoints in `MrWhoOidc.WebAuth` (minimal APIs), and login/consent/logout as Razor Pages.
- Default endpoints:
  - `/.well-known/openid-configuration`, `/jwks`, `/authorize`, `/token`, `/userinfo`, `/logout`.

## Security and quality
- Use Argon2id (or BCrypt) for password hashing. Do not store plaintext secrets.
- Add input validation for all protocol parameters. Return RFC-compliant error responses.
- Prefer dependency injection and interfaces to keep protocol logic testable.
- Leave clear TODOs where stubs are used (e.g., temporary in-memory values).
- **Zero-warning policy**: All code must compile with zero warnings. Address compiler and analyzer warnings before submitting.

## Observability
- Use `MrWhoOidc.ServiceDefaults` for logging and OpenTelemetry. Add basic metrics for critical endpoints.

## Documentation
- Update `/docs` when adding features or changing behavior (backlog, ADRs, endpoint examples).
