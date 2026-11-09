# Page: Admin – Add Realm
## Route: /admin/realms/add

## Expectations
- Page heading "Add realm"
- Tenant context banner ("Realm belongs to <tenant> (<slug>)") when a tenant is active
- Form fields:
  - "Name" — required, 2-100 characters, must be unique within the tenant; identifier used
    in issuance and configuration
  - "Display Name" — optional, human-friendly name shown in UIs
  - "Allow unconfirmed email logins" — form-switch with helper text explaining it controls
    whether users with unconfirmed primary emails may complete client sign-in flows
- Action buttons: "Create" (primary) and "Back" (to the realms list)
- Per-field validation messages appear on invalid input; duplicate names report
  "Realm name already exists in this tenant"

## Actions
- Verify page loads without errors
- Verify all three form controls are present
- Verify Create and Back actions are visible
- Validation feedback is not required pre-submit (this is an empty form)

## Visual Checks
- Tenant banner uses the info style with a buildings icon
- The switch uses the form-switch component
- This page is a creation form, NOT a realm list: a table of realms, an existing-realm
  filter, or realm rows are not expected here
