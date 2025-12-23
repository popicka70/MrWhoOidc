# Feature Specification: OIDC Configuration Export/Import

**Feature Branch**: `012-oidc-export-import`  
**Created**: 2024-12-23  
**Status**: Draft  
**Input**: User description: "Extend import functionality to add export capabilities for tenants, realms, clients, and providers with options for obfuscated or full secrets, then enable importing such exports via UI (future API support planned)."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Export Entire Tenant Configuration (Priority: P1)

As a platform administrator, I want to export an entire tenant's configuration (including realms, clients, and identity providers) so that I can back up the configuration, replicate it to another environment, or migrate to a different OIDC instance.

**Why this priority**: This is the most comprehensive export use case and serves as the foundation for all other export scenarios. Tenant export is critical for disaster recovery, environment replication (dev→staging→prod), and migration workflows.

**Independent Test**: Can be fully tested by selecting a tenant in the admin UI, clicking export, and downloading a JSON file that contains all tenant configuration data. The file can be validated for structure and content completeness.

**Acceptance Scenarios**:

1. **Given** a platform admin is on the tenant management page, **When** they select "Export Tenant" for a specific tenant and choose "Export with obfuscated secrets", **Then** a JSON file is downloaded containing the tenant's complete configuration with all secret values replaced by placeholder markers.

2. **Given** a platform admin is on the tenant management page, **When** they select "Export Tenant" and choose "Export with secrets (full)", **Then** a JSON file is downloaded containing the tenant's complete configuration including actual hashed client secrets (with appropriate security warning displayed).

3. **Given** a tenant has multiple realms, clients, and identity providers configured, **When** the tenant is exported, **Then** the export file includes all realms, all clients (with their scopes, redirect URIs, and OBO settings), and all identity providers (with their configurations and claim mappings).

4. **Given** a platform admin exports a tenant, **When** the export completes, **Then** the system logs the export action with the admin's identity, timestamp, and export type (obfuscated/full) for audit purposes.

---

### User Story 2 - Import Tenant Configuration (Priority: P1)

As a platform administrator, I want to import a previously exported tenant configuration so that I can restore a backup, replicate configurations across environments, or migrate from another OIDC instance.

**Why this priority**: Import functionality completes the backup/restore cycle and is equally critical as export. Without import, exports have limited utility.

**Independent Test**: Can be fully tested by uploading a valid export JSON file through the admin UI and verifying that the tenant and all its child entities are created/updated correctly.

**Acceptance Scenarios**:

1. **Given** a platform admin has a valid tenant export file, **When** they upload it via the import interface and the tenant does not exist, **Then** a new tenant is created with all realms, clients, and identity providers from the export file.

2. **Given** a platform admin uploads an export file for an existing tenant slug, **When** the import is processed, **Then** the system prompts for conflict resolution (skip, merge, or overwrite) before proceeding.

3. **Given** an export file contains obfuscated secrets, **When** the file is imported, **Then** the system prompts the admin to provide actual secret values for clients that require secrets, or allows skipping (leaving clients without secrets until manually configured).

4. **Given** an export file contains full secrets, **When** the file is imported, **Then** client secrets are imported as-is (hashed values are preserved).

5. **Given** an import operation fails partway through, **When** an error occurs, **Then** the system rolls back all changes from that import operation and displays a clear error message indicating what failed.

---

### User Story 3 - Export Individual Realm (Priority: P2)

As a tenant administrator, I want to export a single realm's configuration so that I can back up or replicate just that realm without exporting the entire tenant.

**Why this priority**: Provides granular export capability for administrators who manage multiple realms and need to operate on them individually.

**Independent Test**: Can be fully tested by selecting a realm in the admin UI, exporting it, and verifying the downloaded file contains only that realm's configuration and its associated clients.

**Acceptance Scenarios**:

1. **Given** a tenant admin is viewing a realm, **When** they select "Export Realm", **Then** a JSON file is downloaded containing the realm configuration and all clients assigned to that realm.

2. **Given** a realm has multiple clients with various configurations, **When** the realm is exported with obfuscated secrets, **Then** all client secrets are replaced with placeholder markers in the export.

3. **Given** a realm is exported, **When** the export completes, **Then** the file includes metadata identifying the source tenant and realm for reference during import.

---

### User Story 4 - Export Individual Client (Priority: P2)

As a tenant administrator, I want to export a single client's configuration so that I can back up, replicate, or share that specific client configuration.

**Why this priority**: Supports granular management of individual application configurations, useful for sharing client templates or backing up specific applications.

**Independent Test**: Can be fully tested by selecting a client in the admin UI, exporting it, and verifying the downloaded file contains only that client's complete configuration.

**Acceptance Scenarios**:

1. **Given** a tenant admin is viewing a client's details, **When** they select "Export Client", **Then** a JSON file is downloaded containing the client's complete configuration including scopes, redirect URIs, OBO settings, and identity provider assignments.

2. **Given** a client has a client secret configured, **When** exported with obfuscated secrets, **Then** the secret is replaced with a placeholder marker.

3. **Given** a client has a client secret configured, **When** exported with full secrets, **Then** the hashed secret value is included (not plaintext - secrets are never stored in plaintext).

---

### User Story 5 - Export Individual Identity Provider (Priority: P2)

As a tenant administrator, I want to export a single identity provider's configuration so that I can replicate IdP settings across tenants or back up a specific provider configuration.

**Why this priority**: Identity providers often have complex configurations that benefit from export/import capabilities for replication and backup.

**Independent Test**: Can be fully tested by selecting an identity provider in the admin UI, exporting it, and verifying the downloaded file contains the complete IdP configuration.

**Acceptance Scenarios**:

1. **Given** a tenant admin is viewing an identity provider, **When** they select "Export Provider", **Then** a JSON file is downloaded containing the IdP's configuration including type, claim mappings, and settings.

2. **Given** an IdP has client credentials configured (for OIDC federation), **When** exported with obfuscated secrets, **Then** the client secret is replaced with a placeholder marker.

3. **Given** an IdP has signing keys configured, **When** the IdP is exported, **Then** public key information is included but private keys are excluded (or obfuscated based on export type).

---

### User Story 6 - Import Realm Configuration (Priority: P3)

As a tenant administrator, I want to import a realm configuration into my tenant so that I can add realms from backups or replicate realm configurations.

**Why this priority**: Complements realm export functionality and supports realm-level restoration.

**Independent Test**: Can be fully tested by uploading a realm export file and verifying the realm and its clients are created in the target tenant.

**Acceptance Scenarios**:

1. **Given** a tenant admin has a valid realm export file, **When** they upload it to their tenant, **Then** a new realm is created (or existing updated based on conflict resolution) with all included clients.

2. **Given** the import file references a realm name that already exists, **When** import is attempted, **Then** the admin is prompted to rename, merge, or overwrite.

---

### User Story 7 - Import Individual Client (Priority: P3)

As a tenant administrator, I want to import a client configuration into a realm so that I can add clients from backups or replicate client configurations.

**Why this priority**: Supports client-level restoration and replication workflows.

**Independent Test**: Can be fully tested by uploading a client export file, selecting a target realm, and verifying the client is created with all its configuration.

**Acceptance Scenarios**:

1. **Given** a tenant admin has a valid client export file, **When** they upload it and select a target realm, **Then** a new client is created with the exported configuration.

2. **Given** the import file references a client_id that already exists in the tenant, **When** import is attempted, **Then** the admin is prompted to rename, merge, or overwrite.

---

### User Story 8 - Import Individual Identity Provider (Priority: P3)

As a tenant administrator, I want to import an identity provider configuration so that I can add or restore IdP configurations.

**Why this priority**: Complements IdP export functionality.

**Independent Test**: Can be fully tested by uploading an IdP export file and verifying the provider is created in the target tenant.

**Acceptance Scenarios**:

1. **Given** a tenant admin has a valid IdP export file, **When** they upload it, **Then** a new identity provider is created with the exported configuration.

2. **Given** the import file references a provider name that already exists, **When** import is attempted, **Then** the admin is prompted to rename, merge, or skip.

---

### Edge Cases

- What happens when an export file references scopes that don't exist in the target environment? → The import should create tenant-scoped scopes or warn about missing global scopes.
- How does the system handle circular references between clients (e.g., OBO relationships)? → Import should process entities in dependency order and handle forward references gracefully.
- What happens when importing into a different OIDC version with schema differences? → Export files include a version number; import should validate compatibility and warn about unsupported features.
- How does the system handle export/import of clients with JWKS configured? → Public keys should be exported; private keys should never be exported.
- What happens when the target tenant has reached its license limits (max clients, max users)? → Import should fail gracefully with a clear message before making any changes.
- How are realm-specific roles handled during export/import? → Roles should be included in realm exports and created during import.

## Requirements *(mandatory)*

### Functional Requirements

**Export Functionality**:

- **FR-001**: System MUST allow platform administrators to export an entire tenant's configuration as a JSON file.
- **FR-002**: System MUST allow tenant administrators to export individual realms as JSON files.
- **FR-003**: System MUST allow tenant administrators to export individual clients as JSON files.
- **FR-004**: System MUST allow tenant administrators to export individual identity providers as JSON files.
- **FR-005**: System MUST support two export modes: "obfuscated secrets" (default) and "full export with secrets".
- **FR-006**: In obfuscated mode, System MUST replace all secret values with a recognizable placeholder marker (e.g., `"***OBFUSCATED***"`).
- **FR-007**: In full export mode, System MUST include hashed secret values (never plaintext secrets).
- **FR-008**: Export files MUST include a version identifier for schema compatibility checking.
- **FR-009**: Export files MUST include metadata (source tenant, export timestamp, export type, exporter identity).
- **FR-010**: System MUST log all export operations with administrator identity, timestamp, and export type for audit purposes.

**Import Functionality**:

- **FR-011**: System MUST allow platform administrators to import tenant configurations from JSON files.
- **FR-012**: System MUST allow tenant administrators to import realm configurations into their tenant.
- **FR-013**: System MUST allow tenant administrators to import client configurations into a specified realm.
- **FR-014**: System MUST allow tenant administrators to import identity provider configurations.
- **FR-015**: System MUST validate import file schema and version compatibility before processing.
- **FR-016**: System MUST detect naming conflicts (duplicate slugs, client_ids, provider names) and prompt for resolution.
- **FR-017**: System MUST support conflict resolution options: skip, rename, merge, or overwrite.
- **FR-018**: System MUST prompt for secret values when importing files with obfuscated secrets.
- **FR-019**: System MUST execute imports within a transaction, rolling back all changes if any error occurs.
- **FR-020**: System MUST validate license limits before import and fail gracefully if limits would be exceeded.
- **FR-021**: System MUST log all import operations with administrator identity, timestamp, and outcome for audit purposes.

**UI Requirements**:

- **FR-022**: Export buttons MUST be accessible from tenant, realm, client, and identity provider management pages.
- **FR-023**: Import interface MUST support file upload with drag-and-drop functionality.
- **FR-024**: Import interface MUST display a preview of what will be created/modified before executing.
- **FR-025**: Import interface MUST display clear progress and status during import operations.
- **FR-026**: Import interface MUST display detailed error messages when validation or import fails.

**File Format**:

- **FR-027**: Export files MUST use JSON format with UTF-8 encoding.
- **FR-028**: Export files MUST be compatible with the existing SeedManifest schema structure for consistency.
- **FR-029**: Export files MUST support forward-compatible schema evolution (unknown properties should be ignored during import).

### Key Entities

- **ExportManifest**: Root container for exported configuration; includes version, metadata, export type, and entity payload.
- **TenantExport**: Complete tenant definition including all child entities (realms, clients, identity providers, scopes, roles).
- **RealmExport**: Realm definition with associated clients and roles.
- **ClientExport**: Complete client configuration including scopes, redirect URIs, OBO settings, and IdP assignments.
- **IdentityProviderExport**: Identity provider definition including type, configuration, claim mappings, and (public) keys.
- **ExportMetadata**: Audit information including source system, timestamp, export type, and administrator identity.
- **ImportResult**: Summary of import operation including created/updated/skipped counts and any errors.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Administrators can complete a full tenant export in under 30 seconds for tenants with up to 100 clients.
- **SC-002**: Administrators can complete a tenant import (with preview) in under 2 minutes for typical configurations.
- **SC-003**: 100% of export files produced by the system can be successfully imported back into the same or another instance.
- **SC-004**: Export/import round-trip preserves all configuration data exactly (except for auto-generated IDs which may differ).
- **SC-005**: Zero plaintext secrets are ever written to export files, regardless of export type selected.
- **SC-006**: All export and import operations are fully auditable through system logs.
- **SC-007**: Users successfully complete export operations on first attempt 95% of the time (no confusion about UI workflow).
- **SC-008**: Users successfully complete import operations on first attempt 85% of the time (allowing for conflict resolution decisions).
- **SC-009**: Import failure due to validation errors provides actionable error messages that enable users to correct issues without support intervention.

## Assumptions

- The existing SeedManifest schema in `MrWhoOidc.Auth.Seeding` provides a suitable foundation for export file structure and can be extended for additional export metadata.
- Export/import functionality will initially be UI-only; API endpoints and environment-file-driven imports are future enhancements.
- Hashed secrets (not plaintext) are acceptable for "full export" mode since the system never stores plaintext secrets.
- Identity provider private keys are never exported; only public key material is included in exports.
- The feature will respect existing RBAC: platform admins for tenant-level operations, tenant admins for realm/client/IdP operations.
- License limit checks are performed at import time to prevent exceeding subscription limits.

