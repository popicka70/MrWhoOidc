# Feature Specification: Identity Provider Configuration Form

**Feature Branch**: `009-provider-form-ui`  
**Created**: 2025-12-14  
**Status**: Draft  
**Input**: User description: "Improve identity providers UI. Today add/edit expects JSON for client id/secret/etc. Replace with a real form with inputs + validations for all standard parameters; keep JSON only for extended parameters."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Add OIDC provider using guided form (Priority: P1)

As an admin, I want to add an identity provider by filling in clear, labeled inputs (instead of hand-writing configuration JSON) so that configuring external sign-in is fast and error-resistant.

**Why this priority**: Provider configuration is an admin “setup gate” and is currently blocked by an error-prone manual step (writing JSON).

**Independent Test**: Can be fully tested by creating a new provider using only the form inputs (no advanced JSON) and verifying it saves successfully with validation feedback for invalid entries.

**Acceptance Scenarios**:

1. **Given** I am an authorized admin on the “Add Identity Provider” page and provider type is OIDC, **When** I fill required standard fields with valid values and click Save, **Then** the provider is created and the UI confirms success.
2. **Given** I am on the “Add Identity Provider” page, **When** I leave a required standard field blank or enter an invalid value (e.g., malformed URL) and click Save, **Then** I see clear validation messages tied to the relevant inputs and the provider is not created.
3. **Given** I am on the “Add Identity Provider” page, **When** I do not provide any advanced configuration, **Then** I can still complete provider creation using only standard inputs.

---

### User Story 2 - Edit provider without breaking existing configurations (Priority: P2)

As an admin, I want to edit an existing identity provider using the same standard inputs so I can safely update settings without rewriting or losing existing configuration.

**Why this priority**: Existing tenants/providers must remain manageable; edits are frequent (secret rotation, toggling PKCE/PAR, etc.).

**Independent Test**: Can be fully tested by opening an existing provider, confirming fields are populated, changing a standard value, saving, and verifying the update persists while extended configuration is preserved.

**Acceptance Scenarios**:

1. **Given** an existing OIDC provider with stored configuration, **When** I open its edit page, **Then** the standard inputs are populated from the stored configuration when possible.
2. **Given** an existing provider, **When** I update one standard field and Save, **Then** only that setting changes and unrelated stored configuration remains intact.
3. **Given** an existing provider with an invalid or non-parseable configuration blob, **When** I open the edit page, **Then** I can still view and correct it without losing data.

---

### User Story 3 - Use advanced configuration only when needed (Priority: P3)

As an admin, I want a place to input optional/extended configuration so I can handle provider-specific parameters without cluttering the standard form.

**Why this priority**: Some providers require non-standard parameters; keeping them separate maintains usability while retaining flexibility.

**Independent Test**: Can be fully tested by adding a provider using standard fields plus an advanced configuration block, and verifying invalid advanced configuration is rejected with specific feedback.

**Acceptance Scenarios**:

1. **Given** I need to include an extended parameter not represented by a standard input, **When** I enter it in the advanced configuration area and Save, **Then** it is saved and will be applied for sign-in flows.
2. **Given** I enter malformed advanced configuration, **When** I click Save, **Then** I see a clear error message describing what is invalid and the provider is not saved.

---

### Edge Cases

- Provider configuration contains unknown/extended keys not supported by the standard form: ensure these are preserved across edit/save.
- Secret handling on edit: changing the secret should be explicit and should not accidentally blank or reveal the existing secret.
- Standard and advanced configuration conflict (same setting provided in both): ensure a deterministic outcome and clear feedback.
- Large numbers of scopes or long values: input remains usable and validations remain clear.
- Invalid URL inputs (authority/discovery/logo): show field-level feedback and block save.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: For OIDC identity providers, the add/edit UI MUST provide dedicated inputs for each standard configuration field, with clear labels and help text: Authority (provider base URL), Discovery URL (optional metadata URL), Client ID, Client Secret (optional), Response Type (authorization flow), Scopes (permissions list), Use PKCE (recommended protection), Use JAR (signed request option), Use PAR (pushed request option), Requested ACR values (optional assurance request), Prompt (optional sign-in prompt behavior), Response Mode (optional response delivery mode), Clock Skew Seconds (time tolerance), Token Validation options (issuer/audience/lifetime checks), and Back-Channel Logout (logout notification preference).
- **FR-002**: The UI MUST validate standard configuration inputs with clear, field-level errors at save time.
- **FR-003**: The system MUST enforce required fields for standard configuration (at minimum: Authority and Client ID) and MUST prevent saving when required fields are missing.
- **FR-004**: The system MUST validate URL-shaped inputs (Authority and, when provided, Discovery URL and Logo URL) and MUST prevent saving if invalid.
- **FR-005**: The system MUST support entering scopes as a user-friendly list (not requiring a user to format a structured configuration document).
- **FR-006**: The UI MUST include an advanced configuration area intended only for extended/non-standard parameters.
- **FR-007**: Advanced configuration MUST be optional and MUST NOT be required to successfully create or edit a provider using only standard inputs.
- **FR-008**: Advanced configuration input MUST be syntactically validated and saving MUST be blocked with a clear error message when invalid.
- **FR-009**: When editing an existing provider, the UI MUST populate standard inputs from stored configuration when possible.
- **FR-010**: When an existing provider contains unknown/extended configuration fields, the system MUST preserve them across edit/save cycles unless the admin explicitly removes them.
- **FR-011**: If the stored configuration is invalid or cannot be mapped cleanly to standard inputs, the UI MUST still allow admins to view and fix the configuration without data loss.
- **FR-012**: Secret handling MUST be safe by default: the UI MUST NOT display the current secret value, and it MUST be possible to keep the existing secret unchanged when saving edits.
- **FR-013**: If a value is provided both in standard inputs and in advanced configuration for the same setting, the system MUST prefer the standard input value and MUST present a clear warning or validation message explaining the conflict.

### Key Entities *(include if feature involves data)*

- **Identity Provider**: Admin-managed external provider record including name, display name, enabled/default flags, sort order, logo URL, type, and provider configuration.
- **OIDC Provider Configuration**: Standardized set of provider settings (authority/discovery, client credentials, scopes, auth-flow flags, timing tolerance, token validation preferences, back-channel logout preference).
- **Extended Parameters**: Optional key/value or structured settings for provider-specific behaviors beyond the standard configuration.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: At least 90% of admins can successfully create an OIDC provider on the first attempt without editing the advanced configuration.
- **SC-002**: Provider configuration save failures attributable to invalid structured configuration text decrease by at least 80% within 30 days of release.
- **SC-003**: Median time for an admin to create an OIDC provider (from opening the add page to successful save) is under 3 minutes.
- **SC-004**: Support requests related to “how to format provider configuration” decrease by at least 50% within 60 days of release.

## Assumptions

- The scope of this feature is the identity provider add/edit experience for the OIDC provider type.
- Standard configuration fields are defined by the existing OIDC provider configuration schema used by the system today.
- Advanced configuration remains available for extended parameters, but standard usage should not require it.

## Glossary

- **OIDC**: A standard way to sign users in using an external identity provider.
- **Standard configuration**: Common fields most providers require (URLs, client credentials, scopes, and common options).
- **Advanced configuration**: Optional structured configuration text used only for extended/non-standard parameters.

