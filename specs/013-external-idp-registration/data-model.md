# Data Model: External IdP Registration

**Feature**: 013-external-idp-registration  
**Date**: 2025-12-25

## Entity Changes

### IdentityProvider (Modified)

Extends the existing `IdentityProvider` entity in `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`.

```text
IdentityProvider
├── Id: Guid (PK)                    # Existing
├── TenantId: Guid (FK)              # Existing
├── Name: string (150)               # Existing - unique key
├── DisplayName: string? (200)       # Existing
├── Type: IdentityProviderType       # Existing
├── Enabled: bool                    # Existing - default true
├── IsDefault: bool                  # Existing - default false
├── AllowRegistration: bool          # NEW - default false
├── LogoUrl: string? (2000)          # Existing
├── SortOrder: int                   # Existing - default 0
├── ConfigJson: string? (8000)       # Existing
├── CreatedAt: DateTimeOffset        # Existing
└── UpdatedAt: DateTimeOffset        # Existing
```

#### New Property

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `AllowRegistration` | `bool` | `false` | When true, this IdP appears on the public registration page |

#### Validation Rules

- `AllowRegistration` can only be `true` when `Enabled` is also `true` (enforced in admin UI)
- The public registration page displays registration-enabled IdPs from the default registration tenant; the resulting enrollment target can still come from tenant creation, invitation, domain claim, or client policy.

### No New Entities

This feature modifies an existing entity only. No new entities are introduced.

---

## Database Migration

### Migration Name

`AddAllowRegistrationToIdentityProvider`

### SQL (PostgreSQL)

```sql
ALTER TABLE "IdentityProviders" 
ADD COLUMN "AllowRegistration" BOOLEAN NOT NULL DEFAULT FALSE;
```

### EF Core Migration Command

```bash
dotnet ef migrations add AddAllowRegistrationToIdentityProvider \
  --project MrWhoOidc.Auth \
  --startup-project MrWhoOidc.WebAuth \
  --output-dir Persistence/Migrations
```

---

## Query Patterns

### Get Registration-Enabled IdPs

Used by the registration page to display IdP options.

```csharp
var defaultTenantId = await db.Tenants
    .Where(t => t.IsDefault)
    .Select(t => t.Id)
    .FirstOrDefaultAsync(ct);

var idps = await db.IdentityProviders
    .AsNoTracking()
    .Where(p => p.TenantId == defaultTenantId 
             && p.Enabled 
             && p.AllowRegistration)
    .OrderBy(p => p.SortOrder)
    .ThenBy(p => p.DisplayName ?? p.Name)
    .Select(p => new RegistrationIdpOption
    {
        Name = p.Name,
        DisplayName = p.DisplayName ?? p.Name,
        LogoUrl = p.LogoUrl
    })
    .ToListAsync(ct);
```

### DTO for Registration Page

```csharp
public sealed record RegistrationIdpOption
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? LogoUrl { get; init; }
}
```

---

## State Transitions

### IdP Registration Flow States

```text
                                ┌─────────────────────────┐
                                │  Registration Page      │
                                │  (shows IdP buttons)    │
                                └───────────┬─────────────┘
                                            │
                                            │ User clicks IdP button
                                            ▼
                                ┌─────────────────────────┐
                                │  External IdP Start     │
                                │  (/Auth/External/Start) │
                                └───────────┬─────────────┘
                                            │
                                            │ Redirect to external IdP
                                            ▼
                                ┌─────────────────────────┐
                                │  External IdP           │
                                │  (user authenticates)   │
                                └───────────┬─────────────┘
                                            │
                                            │ Callback with code
                                            ▼
                                ┌─────────────────────────┐
                                │  External Callback      │
                                │  (/Auth/External/       │
                                │   Callback)             │
                                └───────────┬─────────────┘
                                            │
                           ┌────────────────┼────────────────┐
                           │                │                │
                           ▼                ▼                ▼
                   ┌───────────┐    ┌───────────┐    ┌───────────┐
                   │ New User  │    │ Existing  │    │ Error     │
                   │ Created   │    │ Account   │    │ (missing  │
                   └─────┬─────┘    │ Found     │    │ claims)   │
                         │          └─────┬─────┘    └─────┬─────┘
                         │                │                │
                         ▼                ▼                ▼
                   ┌───────────┐    ┌───────────┐    ┌───────────┐
                   │ Success   │    │ Login     │    │ Error     │
                   │ Page      │    │ Instead   │    │ Page      │
                   └───────────┘    └───────────┘    └───────────┘
```

---

## Relationships

```text
Tenant (1) ──────< IdentityProvider (*)
    │
    │ Default tenant's IdPs with AllowRegistration=true
    │ appear on public registration page
    │
    ▼
Registration Page
```

---

## Indexes

No new indexes required. Existing indexes on `IdentityProvider.TenantId` and `IdentityProvider.Name` are sufficient.

The query pattern filters by `TenantId`, `Enabled`, and `AllowRegistration`—all columns with low cardinality that don't benefit from additional indexing given the small row count per tenant.

---

## Seeding Updates

### SeedManifest Extension

The `IdentityProviderSeedDefinition` in `MrWhoOidc.Auth/Seeding/SeedManifest.cs` should include the new property:

```csharp
public sealed record IdentityProviderSeedDefinition
{
    // Existing properties...
    
    [JsonPropertyName("allowRegistration")]
    public bool? AllowRegistration { get; init; }
}
```

### Import/Export

Update `ConfigurationExportService` and `ConfigurationImportService` to handle the new property (follows existing patterns for other IdP properties).
