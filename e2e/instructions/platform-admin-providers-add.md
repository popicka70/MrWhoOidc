# Page: Platform Admin – Add Provider
## Route: /platform-admin/providers/add

## Expectations
- Page header "Add Platform Identity Provider" with subtitle about platform sign-in
- Form organized in section cards:
  - "Provider": Name, Display name, Enabled toggle, Default provider toggle,
    Display order
  - "OIDC Configuration": Authority (issuer URL), Discovery URL, Client ID,
    Client secret (masked input), Scopes
  - "Button Style": Button background/text color with color pickers and hex inputs
- Save (primary) and Back/cancel actions

## Actions
- Verify page loads without errors
- Verify the three sections render with their fields
- Verify Save and Back actions are visible

## Visual Checks
- Field labels are human-readable ("Display order", "Client ID", "Client secret") —
  raw property names like "SortOrder" are a defect
- Secret input is masked; hex colors pair a native color picker with the text input
- This page is a creation form, NOT a provider list
