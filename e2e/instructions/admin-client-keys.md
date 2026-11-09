# Page: Admin – Client Keys (JWKS)
## Route: /admin/client-keys

## Expectations
- Page heading "Client Keys — <client>" with a key icon and a short explanation of what
  the client JWKS is for (signing request objects, publishing at the client's jwks_uri)
- "Back to client" link to the client edit page
- Left column "Current JWKS" card:
  - "Fetch from URI" input + Fetch JWKS button
  - "Public JWKS JSON" textarea (monospace)
  - Validation status badge (Valid/Invalid) with a message
  - Save JWKS (primary) action; on save a snapshot is archived in the history
- Right column "Active keys" card:
  - Table of the current keys: Key ID (kid), Type (kty), Use, Algorithm — or a friendly
    empty state when no keys are configured
- "History" card below with rows: When, Source badge, Hash, Summary, Restore action —
  or a friendly empty state when no snapshots exist

## Actions
- Verify page loads without errors
- Verify the fetch row and the JWKS textarea are present
- Verify the Active keys and History cards render (empty states are acceptable)

## Visual Checks
- Status badges use color coding (green=valid, red=invalid)
- Key IDs and algorithms rendered as <code>
- This page manages ONE client's signing keys: the client list, other clients, or realm
  configuration are not expected here
