# Page: Admin – Add Identity Provider
## Route: /admin/providers/add

## Expectations
- Page heading "Add Identity Provider"
- A multi-step form organized in cards:
  - "Step 1: Select Provider Template": template picker for well-known providers
    (Google, GitHub, Microsoft/Entra, Apple, ...) or Custom/Generic OIDC
  - "Step 2: Basic Information": name, display name, enabled, logo upload,
    button colors (native color pickers paired with hex text inputs)
  - "Step 3: Provider Configuration": dynamic fields based on the selected template
    — for Custom OIDC: authority/issuer, client ID, client secret, scopes; for
    well-known providers: client ID/secret plus pre-filled endpoints
  - Save/Create (primary) and back actions
- Validation summary and per-field validation feedback

## Actions
- Verify page loads without errors
- Verify all three step cards render
- Verify the template picker is selectable
- Verify Save and back actions are visible

## Visual Checks
- Provider templates presented as selectable cards/tiles, not a bare radio list
- Logo upload accepts common image formats; color fields pair picker + hex input
- This page is a creation wizard, NOT a provider list
