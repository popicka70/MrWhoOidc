# Quickstart: Identity Provider Configuration Form

**Branch**: 009-provider-form-ui  
**Date**: 2025-12-14

## Goal

Verify the admin experience for adding/editing OIDC identity providers uses a standard form (not hand-written configuration JSON), with validations and safe secret handling.

## Prerequisites

- .NET 9 SDK installed
- A local environment that can run the solution (Aspire recommended)

## Run

1. Start the system using the existing local workflow (e.g., the Aspire host).
2. Sign in as a user that has `tenant-admin` privileges.

## Manual Verification

### Add Provider

1. Navigate to Admin → Providers → Add Identity Provider.
2. Choose provider type OIDC.
3. Fill required standard fields (Authority and Client ID).
4. Save.

Expected:

- Save succeeds without requiring any advanced JSON.
- Invalid inputs show field-level validation messages.

### Edit Provider

1. Open an existing OIDC provider.
2. Confirm standard fields are populated.
3. Change a standard field and save.

Expected:

- Changes persist.
- Any existing extended/unknown configuration is preserved.

### Secret Handling

1. Edit an existing provider.
2. Leave “New client secret” empty.
3. Save.

Expected:

- The stored secret remains unchanged.

## Automated Verification

- Run the repository test task `test-mrwhooidc`.
