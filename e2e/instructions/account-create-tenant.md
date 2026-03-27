# Page: Account – Create Tenant
## Route: /account/create-tenant

## Expectations
- Page heading "Create Tenant" or "New Organization" with a building or plus icon
- Form fields: Tenant Name, Tenant Slug (subdomain-safe identifier), optional description
- Slug field with auto-suggest from tenant name (JavaScript suggestion)
- Submit button "Create Tenant" or "Create Organization"
- Cancel/Back link to return without creating
- Validation: slug must be lowercase alphanumeric, hyphens allowed, unique
- Info text explaining what a tenant is (workspace/organization)

## Actions
- Verify page loads without errors
- Verify all form fields are visible and labeled
- Verify the Create button is present and styled correctly
- Verify the Cancel/Back navigation link works

## Visual Checks
- Page header with Phosphor icon (ph ph-buildings or ph ph-plus-circle)
- Form uses standard Bootstrap mb-3 + form-label layout
- Slug field should show character restrictions (lowercase, hyphens)
- Submit button uses btn-primary
