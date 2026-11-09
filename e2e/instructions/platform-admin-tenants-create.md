# Page: Platform Admin – Add Tenant
## Route: /platform-admin/tenants/create

## Expectations
- Page heading "Add Tenant" with a plus icon and subtitle "Set up a new tenant instance"
- "Back to Tenants" link
- Form fields (with required markers where applicable):
  - Tenant slug/identifier (required)
  - Tenant name (required)
  - Admin email / admin user fields
  - Limits (max users, max clients) and billing plan
  - Status select (Active/Suspended)
  - Optional branding: logo URL, primary color, accent color (hex validation)
  - "Create Tenant" (primary/success) and cancel/back actions
- Validation feedback on invalid input

## Actions
- Verify page loads without errors
- Verify the main fields (slug, name, admin email) are present
- Verify Create and Back actions are visible

## Visual Checks
- Fields grouped into logical sections/cards
- This page is a creation form, NOT a tenant list: a table of tenants, an Import
  Tenants button, or tenant rows are not expected here
