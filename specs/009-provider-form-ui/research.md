# Research: Identity Provider Configuration Form

**Branch**: 009-provider-form-ui  
**Date**: 2025-12-14  
**Spec**: [spec.md](spec.md)

## Decision 1: Preserve unknown/extended configuration keys

**Decision**: When saving changes from the standard form, preserve any existing configuration keys that are not represented by standard form fields.

**Rationale**: The current OIDC config JSON is used as a flexible configuration container. Deserializing into a typed model and re-serializing loses unknown keys, which violates the requirement to avoid data loss across edits.

**Alternatives considered**:

- **A: Replace full config with form serialization** (current behavior on edit): Simple but loses unknown keys.
- **B: Maintain separate “extended config” field in storage**: Clean separation but requires schema and migration work; increases operational complexity.
- **C: Merge on save** (chosen): Read existing JSON object, update/overwrite only known standard keys from the form, and keep unknown keys intact.

**Implementation notes (technology-specific)**:

- Avoid the "deserialize typed model → serialize typed model" approach for edit/save because it drops unknown keys.
- Prefer a merge strategy using either:
	- `JsonNode` / `JsonObject` merge, or
	- `JsonDocument` + `Utf8JsonWriter` to copy all unknown properties and then write updated known properties.
- If existing stored config is invalid JSON, do not auto-rewrite it during an edit; require explicit admin intent to replace to avoid accidental data loss.

## Decision 2: Standard vs advanced conflict resolution

**Decision**: If a setting is provided both in standard form inputs and in advanced JSON, the standard input wins and the user receives a clear validation warning/error.

**Rationale**: Standard inputs are the supported pathway and must be deterministic. Allowing advanced JSON to silently override the form undermines usability and predictability.

**Alternatives considered**:

- **A: Advanced JSON overrides standard inputs**: Power-user friendly but confusing and risks accidental misconfiguration.
- **B: Reject on conflict** (acceptable): Prevents ambiguity; can be implemented as validation. (May be used where safe.)
- **C: Standard overrides + warning** (chosen): Deterministic and aligns with “form-first” UX while still allowing advanced parameters.

## Decision 3: Client secret edit behavior

**Decision**: Do not display the stored secret value. The form includes an optional “New client secret” input; leaving it empty keeps the existing secret unchanged.

**Rationale**: Secrets should not be exposed in UI. Accidental blanking on save must be prevented.

**Alternatives considered**:

- **A: Always require secret on edit**: Safer for completeness but highly disruptive and error-prone.
- **B: Show existing secret**: Not acceptable.
- **C: Optional “new secret” field that only updates when provided** (chosen).

**Risks to verify during implementation**:

- Ensure the edit flow does not treat an empty secret input as “set secret to empty/null”.
- Ensure any “details” or “preview” view does not render the secret if the raw config blob contains it.

## Decision 4: Validation strategy

**Decision**: Validate standard fields as first-class inputs (required/URL/range checks) and validate advanced JSON syntactically and semantically (when it targets known fields).

**Rationale**: This provides immediate, field-level feedback and reduces configuration errors.

**Alternatives considered**:

- **A: Validate only by attempting to use configuration at runtime**: Too late and hard to troubleshoot.
- **B: Validate only JSON syntax**: Still allows many incorrect values.
- **C: Field-level validations + structured config validation on save** (chosen).
