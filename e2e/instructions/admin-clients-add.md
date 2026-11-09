# Page: Admin – Add Client
## Route: /admin/clients/add

## Expectations
- Page heading "Add client"
- Form fields:
  - "Client ID" (required, 2-200 chars) with a Generate button in an input group
  - "Client name" (optional)
  - "Realm" select (required)
  - Client type quick-pick button group: Web app (confidential) / SPA-mobile (public) / Service (M2M)
  - Grant type checkboxes: Authorization code (+ refresh token), Client credentials (M2M),
    Device authorization, CIBA, Token exchange (OBO)
  - "Client secret" input (masked) with Generate / reveal / Copy buttons and
    "Leave empty for public client" help
  - Collapsible "OIDC JWT response crypto (optional)" advanced card with signing/encryption selects
  - Post-creation info banner explaining that allowed sign-in/sign-out redirect URIs and scopes
    are configured on the client's edit page after creation
- "Create" (primary) and "Back" (to the client list) actions

## Actions
- Verify page loads without errors
- Verify Client ID, Client name, and Realm controls are present
- Verify the client-type quick-pick and grant type checkboxes are visible
- Verify Create and Back actions are visible

## Visual Checks
- Quick-pick buttons use the btn-group component
- The secret input is masked (type password) with helper buttons
- This page is a creation form, NOT a client list: a table of clients, Export/Import
  controls, or pagination are not expected here
