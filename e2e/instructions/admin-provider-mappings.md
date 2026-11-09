# Page: Admin – Client ↔ Provider Mappings
## Route: /admin/provider-mappings

## Expectations
- Page heading "Client ↔ Provider Mappings"
- Tenant context banner when multi-tenancy is enabled
- A mapping form:
  - Client select, Identity Provider select, Order input (each with form-text help)
  - Behavior checkbox group: Enabled, Default, Auto-redirect if single — grouped under
    a "Behavior" label with help text
  - "Add / Update" (primary) action
- A table of existing mappings: Client, Provider, Enabled, Default, Auto, Order,
  with remove actions — or a friendly empty state with a two-arrows icon explaining
  what mappings do

## Actions
- Verify page loads without errors
- Verify the mapping form controls are present
- Verify the mappings table or its empty state is visible

## Visual Checks
- Checkboxes align with their group label, not floating in the grid
- Mapping rows use consistent badges/icons for Enabled/Default/Auto
