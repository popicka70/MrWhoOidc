# Page: Admin – Edit Client
## Route: /admin/clients/edit

## Expectations
- Page heading "Edit client" (or the client's name)
- Tabbed sections with the client's configuration, including at least:
  - General (client ID, name, realm, PKCE/consent/PAR switches)
  - Redirect URIs (allowed sign-in and sign-out redirect URIs, one per line, with
    an info banner about exact matching)
  - Providers, Scopes, Keys (JWKS), Secrets, Introspection, M2M, OBO, Users, Tools tabs
  as applicable
- All fields prefilled with the client's current values
- Save (primary) and Back/cancel actions
- Flash messages for success/error outcomes

## Actions
- Verify page loads without errors
- Verify the tab strip renders and the General tab is active with prefilled values
- Verify Save and Back actions are visible

## Visual Checks
- The tab strip uses nav-tabs with the active tab highlighted
- Advanced sections are progressively disclosed (collapse) rather than one long wall of fields
- This page is a client EDIT form, NOT a client list: a table of clients, an "Add Client"
  button, Export/Import controls, or pagination are not expected here
