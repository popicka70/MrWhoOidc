# Page: Admin – Edit Realm
## Route: /admin/realms/edit

## Expectations
- Page heading "Edit realm"
- Tenant context banner ("Tenant: <name>") when applicable
- Form fields prefilled with the realm's current values:
  - "Name" — the realm identifier (2-100 chars, unique in tenant), editable
  - "Display Name" — optional human-friendly name
  - "Allow unconfirmed email logins" — form-switch with helper text about email
    confirmation requirements
- Action buttons: "Save" (primary) and "Back" (to the realms list)
- A validation summary or per-field validation messages appear on invalid input

## Actions
- Verify page loads without errors
- Verify the form is prefilled with the realm's existing values
- Verify Save and Back actions are visible

## Visual Checks
- Tenant banner uses the info style with a buildings icon
- The switch uses the form-switch component with explanatory form-text
- This page is an edit form, NOT a realm list: a table of realms, an "Add Realm"
  button, or realm rows are not expected here
