# Page: Platform Admin – Edit Tenant
## Route: /platform-admin/tenants/edit

## Expectations
- Page heading "Edit Tenant" with an icon and the tenant's name visible
- Tenant status badges/summary where shown
- Form prefilled with the tenant's current values:
  - Name, slug/identifier, admin email
  - Status, limits (max users / max clients), billing plan
  - Branding options (logo URL, colors)
- Save (primary) and Back/cancel actions
- A danger zone or delete control may be present with appropriate confirmation affordances

## Actions
- Verify page loads without errors
- Verify the form is prefilled with the tenant's existing values
- Verify Save and Back actions are visible

## Visual Checks
- Sections/cards group related fields
- This page is an EDIT form for ONE tenant: the tenant list table, an "Add Tenant"
  button, or Import controls are not expected here
