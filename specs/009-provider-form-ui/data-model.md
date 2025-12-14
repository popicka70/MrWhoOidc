# Data Model: Identity Provider Configuration Form

**Branch**: 009-provider-form-ui  
**Date**: 2025-12-14  
**Spec**: [spec.md](spec.md)

## Entities

### Identity Provider

Represents an externally configured identity provider available to a tenant.

**Key fields** (conceptual):

- `Id`: Unique identifier
- `TenantId`: Owning tenant
- `Name`: Admin-facing name (unique within tenant)
- `DisplayName`: Optional friendly label
- `Type`: Provider type (OIDC, SAML)
- `Enabled`: Whether available for sign-in
- `IsDefault`: Whether preferred by default
- `SortOrder`: Display order
- `LogoUrl`: Optional image reference
- `ConfigJson`: Provider configuration container (structured text)
- `CreatedAt`, `UpdatedAt`: Audit timestamps

### OIDC Provider Configuration (standard)

Represents the subset of settings exposed as first-class inputs.

**Standard fields**:

- Authority (required)
- Discovery URL (optional)
- Client ID (required)
- Client secret (optional; update-only)
- Response type
- Scopes (list)
- Use PKCE
- Use JAR
- Use PAR
- Requested ACR values (optional)
- Prompt (optional)
- Response mode (optional)
- Clock skew seconds
- Token validation options (issuer/audience/lifetime)
- Back-channel logout

### Extended parameters (advanced)

Represents any provider-specific or less-common settings not covered by standard fields.

**Rules**:

- Must be optional.
- Must be preserved across edits unless explicitly removed.
- Must not silently conflict with standard fields.

## Validation Rules (from requirements)

- Authority and Client ID are required.
- URL-shaped fields must be valid URLs.
- Clock skew seconds must be within an allowed range.
- Advanced configuration text must be valid structured text; when it attempts to set known/standard fields, conflicts must be detected.

## State & Transitions

- Create provider → stored with standard config (and optional advanced) → enabled/disabled/default flags can be adjusted later.
- Edit provider → update standard fields and optionally advanced → must not delete unknown keys.
