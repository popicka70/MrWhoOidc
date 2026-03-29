# Page: Select Tenant
## Route: /select-tenant

## Expectations
- Page heading "Select Tenant" or "Choose Organization" visible
- List or grid of tenants the logged-in user belongs to: Tenant name, slug, role(s) in that tenant
- "Continue" or "Select" button per tenant
- If only one tenant exists, may auto-redirect or still show the single option
- Search/filter if many tenants
- "Create new tenant" link if the user is allowed to create tenants

## Actions
- Verify page loads without errors
- Verify at least one tenant is listed for the authenticated test user
- Verify each tenant entry has a select/continue action
- Verify the page title or heading matches expectations

## Visual Checks
- Tenant cards or rows with tenant name prominent
- Role badges (e.g., "admin", "member") shown
- "Select" button uses btn-primary
- Page uses auth-container or centered layout (tenant selection is often a pre-dashboard screen)
