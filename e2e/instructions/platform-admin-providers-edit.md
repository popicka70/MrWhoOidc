# Page: Platform Admin – Edit Provider
## Route: /platform-admin/providers/edit

## Expectations
- Page header "Edit Platform Identity Provider" (or the provider's name)
- Form organized in section cards, prefilled with the provider's current values:
  - "Provider": Name, Display name, Enabled, Default provider, Display order
  - "OIDC Configuration": Authority (issuer URL), Discovery URL, Client ID,
    Client secret (masked), Scopes, with a callback-hint where shown
  - "Button Style": Button background/text colors with color pickers + hex inputs
- Save (primary) and Back/cancel actions

## Actions
- Verify page loads without errors
- Verify the form is prefilled with existing values
- Verify Save and Back actions are visible

## Visual Checks
- Human-readable labels; secret masked; colors use pickers
- This page is an EDIT form, NOT a provider list
