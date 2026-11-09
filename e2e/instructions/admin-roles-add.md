# Page: Admin – Add Role
## Route: /admin/roles/add

## Expectations
- Page heading "Add role" with a short subtitle
- Tenant context banner ("Roles created here automatically belong to <tenant>") when
  multi-tenancy is enabled
- Form fields:
  - "Realm" select (required) with form-text about which realm's users can receive the role
  - "Name" (required) — the policy-facing identifier, with help text that authorization
    matches on this exact name
  - "Display name (optional)" — human-friendly label
  - "Active" checkbox
- "Save" (primary) and "Cancel" (back to roles list) actions

## Actions
- Verify page loads without errors
- Verify Realm and Name controls are present (required markers shown)
- Verify Save and Cancel actions are visible

## Visual Checks
- The form is constrained to a reasonable width (not a full-bleed wide column)
- Required fields marked with a red asterisk
- This page is a creation form, NOT a role list: a table of roles or row actions
  are not expected here
