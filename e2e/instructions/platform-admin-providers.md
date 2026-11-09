# Page: Platform Admin – Identity Providers
## Route: /platform-admin/providers

## Expectations
- Page header "Platform Identity Providers" with a shield icon and subtitle about
  platform-level sign-in
- "Add Provider" (primary) action in the page header
- A table of configured platform providers: name, type/protocol, status, and actions
  (Edit, Delete) per row
- Flash messages for success/error outcomes
- Friendly empty state with guidance when no platform providers are configured yet

## Actions
- Verify page loads without errors
- Verify the Add Provider action is visible
- Verify the provider table or its empty state renders

## Visual Checks
- Status indicators use color coding (enabled=green, disabled=grey)
- Action buttons compact, not overflowing rows
