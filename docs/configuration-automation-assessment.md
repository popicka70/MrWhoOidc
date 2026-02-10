# Configuration Automation Assessment & Implementation

## Overview
This document outlines the assessment and implementation of configuration automation for the MrWhoOidc service. The goal was to enable administrative tasks to be managed without the UI, specifically through declarative configuration files (manifests).

## Solution
The solution leverages the existing `SeedManifest` and `ConfigurationImportService` infrastructure, enhancing them to support user management and exposing the functionality via a CLI command.

### 1. Enhanced Data Model
The `SeedManifest` (specifically `TenantSeedDefinition`) has been updated to include a `Users` property. A new `UserSeedDefinition` class has been introduced to define user attributes:
- **Username**: The unique identifier for the user.
- **Email**: User's email address.
- **Password**: Optional plaintext password (for dev/test).
- **PasswordEnv**: Name of an environment variable containing the password (for secure production usage).
- **Roles**: List of role assignments (scoped to a realm).
- **Clients**: List of client assignments (scoped to a realm).

### 2. Enhanced Import Service
The `ConfigurationImportService` has been updated to process the new `Users` section:
- **User Creation**: Creates both tenant-scoped `User` entities and global `UserAccount` entities (required for authentication).
- **Password Hashing**: Uses the system's `IPasswordHasher` to securely hash passwords provided in the manifest (or via env vars).
- **Assignments**: Handles `UserRealmRoleAssignment` and `UserClientAssignment` creation, linking users to specific roles and clients within specific realms.
- **Role Synchronization**: Updated realm import logic (`EnsureRolesAsync`) to create or update roles defined in the manifest, ensuring the target environment matches the desired state.

### 3. CLI Command
The application entry point (`Program.cs`) has been modified to intercept a `seed` command argument.
- **Usage**: `dotnet run -- seed <path-to-manifest.json>`
- **Behavior**:
    1. Checks if the manifest file exists.
    2. Applies pending database migrations to ensure the schema is up-to-date.
    3. Parses the manifest as an `ExportManifest`.
    4. Invokes `ConfigurationImportService.ImportTenantAsync` to apply the configuration.
    5. Reports success or failure and exits.

## Benefits
- **No UI Required**: Administrators can fully bootstrap a tenant, including users and permissions, using a JSON file.
- **GitOps Ready**: Configuration can be version controlled and applied via CI/CD pipelines using the CLI command.
- **Secure**: Secrets can be injected via environment variables, avoiding hardcoded passwords in configuration files.
- **Idempotent**: The import logic handles updates and deduplication, allowing the same manifest to be applied multiple times safely.
