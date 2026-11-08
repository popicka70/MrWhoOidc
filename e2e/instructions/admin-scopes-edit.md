# Page: Admin – Edit Scope
## Route: /admin/scopes/edit

## Expectations
- Page heading "Edit scope"
- Info banner explaining that this is a GLOBAL scope and its identifier is immutable
- Form fields:
  - "Name" — shown read-only (disabled, monospace), prefilled with the scope's identifier
  - "Description" — editable, prefilled, with placeholder and help text
  - "Exposed" toggle (form-switch) with help text about discovery-document advertisement
- Action buttons: "Save" (btn-primary) and "Cancel" (btn-secondary, returns to the scope list)
- Validation summary support

## Actions
- Verify the form loads with the scope's current values prefilled
- Verify the Name field is disabled/read-only
- Verify Cancel navigates back to /admin/scopes

## Visual Checks
- Scope identifier rendered in a monospace font
- Help text (form-text) under each field
- The Exposed control is a switch, consistent with other admin forms
- Buttons in a gap-spaced group, primary action first
