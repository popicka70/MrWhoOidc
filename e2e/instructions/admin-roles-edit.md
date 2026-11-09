# Page: Admin – Edit Role
## Route: /admin/roles/edit

## Expectations
- Page heading "Edit role"
- Tenant and realm context banner (tenant badge + realm badge)
- For seeded system roles: an info banner explaining that the identifier is immutable
  (authorization policies match on it); the Name field renders read-only
- Form fields:
  - "Realm" select
  - "Name" — read-only for system roles, editable for custom roles
  - "Display name (optional)"
  - "Active" checkbox
- "Save" (primary) and "Back"/cancel actions

## Actions
- Verify page loads without errors
- Verify the form is prefilled with the role's current values
- Verify Save and Back actions are visible

## Visual Checks
- Tenant/realm badges use the info/secondary styles
- This page is an EDIT form for ONE role: a table of roles, an "Add Role" button, or
  row delete actions are not expected here
