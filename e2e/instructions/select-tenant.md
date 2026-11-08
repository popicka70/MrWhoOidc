# Page: Select Tenant
## Route: /select-tenant

## Expectations
- Page heading "Select your organization" visible
- The signed-in user's email shown as subtitle (when available)
- List or grid of tenant cards for the organizations the user belongs to: tenant name and slug, optional logo/icon, optional last-login date
- Each tenant card is clickable (select/continue action)
- "Remember my choice for this browser" checkbox
- "Back to email entry" link
- Anonymous users are redirected to tenant discovery (email entry) — not shown the picker
- If only one tenant exists, the single option is still shown as a card

## Actions
- Verify page loads without errors
- Verify at least one tenant card is listed for the authenticated test user
- Verify each tenant card has a clickable select action (whole card is clickable, keyboard accessible)
- Verify the page title or heading matches expectations

## Visual Checks
- Tenant cards or rows with tenant name prominent
- Card shows the tenant slug under the name
- Consistent icon/logo placeholder when the tenant has no icon
- Page uses auth-card centered layout (tenant selection is a pre-dashboard screen)
