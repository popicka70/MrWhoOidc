# Data Model: OIDC Configuration Export/Import

**Feature**: 012-oidc-export-import  
**Date**: 2024-12-23

## Overview

This document defines the data structures for the export/import feature. The design extends the existing `SeedManifest` schema to support bidirectional configuration management.

---

## 1. Export Manifest (Root Container)

The export manifest wraps the seed manifest with metadata for traceability and validation.

```
ExportManifest
├── schema: string          # Schema identifier URL
├── version: int            # Manifest format version (1)
├── exportType: string      # "tenant" | "realm" | "client" | "provider"
├── exportMode: string      # "obfuscated" | "full"
├── metadata: ExportMetadata
└── data: SeedManifest      # Actual configuration payload
```

### ExportMetadata

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| exportedAt | DateTimeOffset | Yes | UTC timestamp of export |
| exportedBy | string | No | Username who performed export |
| sourceSystem | string | No | System identifier (hostname or instance name) |
| sourceVersion | string | No | MrWhoOidc version at export time |
| sourceTenant | string | No | Tenant slug (for realm/client/provider exports) |
| checksum | string | No | SHA-256 hash of data section for integrity |

---

## 2. Extended SeedManifest Structure

### TenantSeedDefinition (Extended)

```
TenantSeedDefinition
├── slug: string (required)
├── name: string (required)
├── description: string?
├── issuerUri: string?
├── adminEmail: string?
├── billingPlan: string?
├── status: string?              # NEW: "Active" | "Suspended" | "Disabled"
├── logoUrl: string?             # NEW
├── primaryColor: string?        # NEW
├── accentColor: string?         # NEW
├── settingsJson: string?        # NEW: Tenant settings blob
├── maxUsers: int?               # NEW: License limit
├── maxClients: int?             # NEW: License limit
├── realms: RealmSeedDefinition[]
├── clients: ClientSeedDefinition[]
├── identityProviders: IdentityProviderSeedDefinition[]   # NEW
└── roles: RoleSeedDefinition[]                           # NEW
```

### RealmSeedDefinition (Extended)

```
RealmSeedDefinition
├── name: string (required)      # Unique within tenant
├── displayName: string?
└── allowUnconfirmedLogin: bool?
```

### ClientSeedDefinition (Extended)

```
ClientSeedDefinition
├── clientId: string (required)
├── clientName: string (required)
├── realm: string?                          # Realm name (default: "admin")
│
├── # Authentication
├── requirePkce: bool?
├── requireConsent: bool?
├── requirePar: bool?                       # NEW
├── clientSecret: string?                   # Plaintext (import only, dev)
├── clientSecretEnv: string?                # Config key reference
├── clientSecretHash: string?               # NEW: Hashed value (full export)
│
├── # Public Keys
├── publicJwksJson: string?                 # NEW: Inline JWKS
├── publicJwksUri: string?                  # NEW: Remote JWKS URI
│
├── # Redirect URIs
├── allowedLoginRedirectUris: string[]
├── allowedLogoutRedirectUris: string[]
│
├── # Login Methods
├── allowLocalLogin: bool?                  # NEW
├── allowExternalIdp: bool?                 # NEW
├── allowQrLogin: bool?                     # NEW
│
├── # Logout URIs
├── backChannelLogoutUri: string?           # NEW
├── backChannelLogoutSessionRequired: bool? # NEW
├── frontChannelLogoutUri: string?          # NEW
├── frontChannelLogoutSessionRequired: bool?# NEW
│
├── # Scopes & OBO
├── allowedScopes: string[]
├── oboEnabled: bool?
├── oboAllowedSourceAudiences: string[]
├── oboAllowedTargetAudiences: string[]
├── oboAllowedScopes: string[]
├── oboMaxDelegationDepth: int?             # NEW
├── oboMaxLifetimeMinutes: int?             # NEW
├── oboDpopMode: string?                    # NEW: "None" | "Optional" | "Required"
├── oboAllowedCallers: string[]             # NEW
│
├── # M2M Settings
├── m2mAllowedAudiences: string[]           # NEW
├── m2mAccessTokenLifetimeSeconds: int?     # NEW
│
├── # Auto-approval
├── autoApprovalMode: string?               # "No" | "All" | "OnlyExternalIdp"
├── autoAssignNewUsersToClient: bool?       # NEW
│
├── # IdP Assignments
└── identityProviderAssignments: ClientIdpAssignmentSeedDefinition[]  # NEW
```

---

## 3. New Seed Definition Types

### IdentityProviderSeedDefinition

```
IdentityProviderSeedDefinition
├── name: string (required)         # Unique within tenant
├── displayName: string?
├── type: string                    # "oidc" | "saml"
├── enabled: bool?
├── isDefault: bool?
├── logoUrl: string?
├── sortOrder: int?
├── config: Dictionary<string, object>?   # Provider-specific config
├── claimMappings: ClaimMappingSeedDefinition[]
└── keys: ProviderKeySeedDefinition[]
```

#### OIDC Provider Config Structure

When `type` is "oidc", the `config` object contains:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| authority | string | Yes | OIDC issuer URL |
| discoveryUrl | string | No | Override for discovery endpoint |
| clientId | string | Yes | Client ID at upstream IdP |
| clientSecret | string | No | Client secret (obfuscated in exports) |
| responseType | string | No | Default: "code" |
| scopes | string[] | No | Default: ["openid", "profile", "email"] |
| usePKCE | bool | No | Default: true |
| useJAR | bool | No | Default: false |
| usePAR | bool | No | Default: false |
| requestedAcrValues | string | No | ACR values to request |
| prompt | string | No | Prompt parameter |
| responseMode | string | No | Response mode |
| clockSkewSeconds | int | No | Default: 120 |
| backChannelLogout | bool | No | Default: true |
| extraAuthParams | Dictionary | No | Additional parameters |

### ClaimMappingSeedDefinition

```
ClaimMappingSeedDefinition
├── externalClaim: string (required)    # Claim from upstream IdP
├── localClaim: string (required)       # Claim in local token
├── transform: string?                  # Transformation type
└── order: int?
```

### ProviderKeySeedDefinition

```
ProviderKeySeedDefinition
├── purpose: string               # "signing" | "encryption"
├── alg: string                   # Algorithm (e.g., "RS256")
├── kid: string?                  # Key ID
├── jwk: string?                  # Public key JWK (JSON)
└── active: bool?
```

### ClientIdpAssignmentSeedDefinition

```
ClientIdpAssignmentSeedDefinition
├── providerName: string (required)     # Reference to IdP by name
├── enabled: bool?
├── isDefaultForClient: bool?
├── autoRedirectIfSingle: bool?
├── requiredAcr: string?
└── order: int?
```

### RoleSeedDefinition

```
RoleSeedDefinition
├── name: string (required)
├── realmName: string (required)        # Reference to realm by name
└── isActive: bool?
```

### ScopeSeedDefinition (Existing, Unchanged)

```
ScopeSeedDefinition
├── name: string (required)
├── description: string?
├── isGlobal: bool?
├── isExposed: bool?
└── tenantSlug: string?
```

---

## 4. Import/Export Service DTOs

### ExportOptions

```
ExportOptions
├── mode: ExportMode               # Obfuscated | Full
├── includeMetadata: bool          # Default: true
├── includeChecksum: bool          # Default: true
└── prettyPrint: bool              # Default: true (indented JSON)
```

### ImportOptions

```
ImportOptions
├── defaultConflictResolution: ConflictResolution   # Skip | Rename | Merge | Overwrite
├── validateOnly: bool             # Preview without applying
├── dryRun: bool                   # Alias for validateOnly
└── secretProvider: ISecretProvider?   # For resolving obfuscated secrets
```

### ImportPreview

```
ImportPreview
├── isValid: bool
├── validationErrors: ValidationError[]
├── conflicts: ImportConflict[]
├── entitiesToCreate: EntitySummary[]
├── entitiesToUpdate: EntitySummary[]
└── warnings: string[]
```

### ImportConflict

```
ImportConflict
├── type: ConflictType
├── entityType: string             # "Tenant" | "Realm" | "Client" | "Provider"
├── identifier: string             # slug, name, clientId
├── existingEntityId: Guid?
├── suggestedRename: string?
└── resolution: ConflictResolution?   # User's choice
```

### ImportResult

```
ImportResult
├── success: bool
├── entitiesCreated: int
├── entitiesUpdated: int
├── entitiesSkipped: int
├── errors: ImportError[]
├── warnings: string[]
└── auditLogId: Guid?
```

### ValidationError

```
ValidationError
├── path: string                   # JSON path (e.g., "tenants[0].clients[2].clientId")
├── code: string                   # Error code
├── message: string
└── severity: Severity             # Error | Warning
```

---

## 5. Audit Entity

### ConfigurationAuditLog

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| Id | Guid | Yes | PK (UUIDv7) |
| TenantId | Guid? | No | Null for platform-level operations |
| Operation | string | Yes | "Export" or "Import" |
| EntityType | string | Yes | "Tenant", "Realm", "Client", "Provider" |
| EntityIdentifier | string? | No | slug, clientId, name |
| ExportMode | string | Yes | "Obfuscated" or "Full" |
| Result | string | Yes | "Success", "Failed", "PartialSuccess" |
| EntitiesCreated | int? | No | Import only |
| EntitiesUpdated | int? | No | Import only |
| EntitiesSkipped | int? | No | Import only |
| ErrorDetails | string? | No | Error message if failed |
| ManifestChecksum | string? | No | SHA-256 of manifest data |
| PerformedBy | string | Yes | Username |
| PerformedByUserId | Guid? | No | User ID if available |
| IpAddress | string? | No | Request IP |
| UserAgent | string? | No | Request User-Agent |
| Timestamp | DateTimeOffset | Yes | Operation timestamp |

**Indexes**:
- `IX_ConfigurationAuditLog_Tenant_Timestamp` on (TenantId, Timestamp DESC)
- `IX_ConfigurationAuditLog_Operation` on (Operation, Timestamp DESC)

---

## 6. Enumerations

### ExportMode

```
enum ExportMode
{
    Obfuscated = 0,    // Secrets replaced with "***OBFUSCATED***"
    Full = 1           // Include hashed secrets
}
```

### ConflictType

```
enum ConflictType
{
    TenantSlugExists = 0,
    RealmNameExists = 1,
    ClientIdExists = 2,
    ProviderNameExists = 3,
    ScopeNameConflict = 4,
    RoleNameExists = 5
}
```

### ConflictResolution

```
enum ConflictResolution
{
    Skip = 0,       // Do not import conflicting entity
    Rename = 1,     // Auto-rename and import as new
    Merge = 2,      // Update only non-conflicting fields
    Overwrite = 3   // Replace entire entity
}
```

---

## 7. Entity Relationships

```
                                    ┌─────────────────────┐
                                    │   ExportManifest    │
                                    │   (Root Container)  │
                                    └──────────┬──────────┘
                                               │
                              ┌────────────────┴────────────────┐
                              │                                 │
                    ┌─────────▼─────────┐             ┌────────▼────────┐
                    │  ExportMetadata   │             │   SeedManifest  │
                    └───────────────────┘             └────────┬────────┘
                                                               │
                    ┌──────────────────────────────────────────┼───────────────────┐
                    │                                          │                   │
          ┌─────────▼──────────┐                    ┌──────────▼──────┐    ┌──────▼──────┐
          │ TenantSeedDefinition│                    │ ScopeSeedDef    │    │ (global)    │
          └─────────┬──────────┘                    └─────────────────┘    └─────────────┘
                    │
    ┌───────────────┼───────────────┬───────────────────┬───────────────────┐
    │               │               │                   │                   │
┌───▼───┐    ┌──────▼──────┐  ┌─────▼─────┐    ┌───────▼───────┐   ┌───────▼───────┐
│ Realm │    │   Client    │  │   Role    │    │IdentityProvider│   │    Scope     │
│ Seed  │    │    Seed     │  │   Seed    │    │     Seed       │   │ (tenant)     │
└───────┘    └──────┬──────┘  └───────────┘    └───────┬───────┘   └───────────────┘
                    │                                   │
                    │                    ┌──────────────┼──────────────┐
                    │                    │              │              │
              ┌─────▼─────┐       ┌──────▼──────┐ ┌─────▼─────┐ ┌──────▼──────┐
              │ClientIdp  │       │ClaimMapping │ │ProviderKey│ │(config JSON)│
              │Assignment │       │   Seed      │ │   Seed    │ │             │
              └───────────┘       └─────────────┘ └───────────┘ └─────────────┘
```

---

## 8. JSON Schema Example

### Tenant Export (Obfuscated Mode)

```json
{
  "$schema": "https://mrwhooidc.io/schemas/export/v1",
  "version": 1,
  "exportType": "tenant",
  "exportMode": "obfuscated",
  "metadata": {
    "exportedAt": "2024-12-23T10:30:00Z",
    "exportedBy": "admin@example.com",
    "sourceSystem": "mrwhooidc-prod-01",
    "sourceVersion": "1.5.0",
    "checksum": "sha256:abc123..."
  },
  "data": {
    "version": 1,
    "scopes": [
      {
        "name": "custom:read",
        "description": "Read custom resources",
        "isGlobal": false,
        "tenantSlug": "acme"
      }
    ],
    "tenants": [
      {
        "slug": "acme",
        "name": "Acme Corporation",
        "description": "Main tenant",
        "adminEmail": "admin@acme.com",
        "billingPlan": "Enterprise",
        "realms": [
          {
            "name": "admin",
            "displayName": "Administration",
            "allowUnconfirmedLogin": false
          }
        ],
        "clients": [
          {
            "clientId": "web-app",
            "clientName": "Web Application",
            "realm": "admin",
            "requirePkce": true,
            "clientSecretHash": "***OBFUSCATED***",
            "allowedLoginRedirectUris": [
              "https://app.acme.com/callback"
            ],
            "allowedScopes": ["openid", "profile", "custom:read"],
            "identityProviderAssignments": [
              {
                "providerName": "azure-ad",
                "enabled": true,
                "isDefaultForClient": true
              }
            ]
          }
        ],
        "identityProviders": [
          {
            "name": "azure-ad",
            "displayName": "Azure AD",
            "type": "oidc",
            "enabled": true,
            "config": {
              "authority": "https://login.microsoftonline.com/tenant-id/v2.0",
              "clientId": "azure-client-id",
              "clientSecret": "***OBFUSCATED***",
              "scopes": ["openid", "profile", "email"]
            },
            "claimMappings": [
              {
                "externalClaim": "preferred_username",
                "localClaim": "email"
              }
            ]
          }
        ],
        "roles": [
          {
            "name": "viewer",
            "realmName": "admin",
            "isActive": true
          }
        ]
      }
    ]
  }
}
```
