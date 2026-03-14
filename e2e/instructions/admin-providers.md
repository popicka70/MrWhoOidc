# Page: Admin – Identity Provider List
## Route: /admin/providers

## Expectations
- A list/table of external identity providers should be shown
- Each provider row must display: name, type/protocol, status, actions
- "Add Provider" button must be visible
- Export/Import buttons should be accessible

## Actions
- Verify the provider list renders without errors (may be empty on a fresh install)
- Click "Add Provider" — verify the provider type selection or add form renders
- Navigate back
- Click Export — verify no crash

## CRUD Operations
### Add Provider (if form is accessible)
1. Click "Add Provider"
2. Select type (e.g., OIDC)
3. Fill required fields: Name, Authority/Issuer URL, Client ID, Client Secret
4. Submit and verify the provider appears in the list

## Visual Checks
- Empty state should have a helpful illustration/message rather than a blank table
- Provider type badges (OIDC, SAML, etc.) should be visually distinct
- Status indicator (enabled/disabled) should use color coding (green/grey)
- Action buttons should be compact and not overflow the table row
- Claim mappings link should be accessible from the provider detail or edit page
