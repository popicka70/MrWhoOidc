# Page: Admin – Add Scope
## Route: /admin/scopes/add

## Expectations
- Page heading "Add scope"
- Info banner naming the tenant the scope is created for (name and slug)
- Form fields:
  - "Name" — required, validated, with placeholder and help text explaining the dot-separated identifier format (e.g. api.read)
  - "Description" — optional, with placeholder and help text explaining its purpose
  - "Exposed" toggle (form-switch, default on) with help text explaining that exposed scopes are advertised in the tenant's discovery document
- Action buttons: "Save" (btn-primary) and "Cancel" (btn-secondary, returns to the scope list)
- Validation summary and per-field validation messages on invalid input

## Actions
- Verify the form loads with all three fields and the tenant banner
- Verify the Exposed toggle defaults to on
- Verify Cancel navigates back to /admin/scopes

## Visual Checks
- Help text (form-text) under each field
- The Exposed control is a switch, consistent with other admin forms
- Buttons in a gap-spaced group, primary action first
