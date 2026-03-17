# Page: Admin – Scope List
## Route: /admin/scopes

## Expectations
- A list/table of OAuth/OIDC scopes should be shown
- Standard scopes (openid, profile, email, offline_access) must be present
- "Add Scope" button should be visible

## Actions
- Verify standard scopes exist in the list
- Click "Add Scope" — verify the add form renders correctly
- Navigate back
- Click "Edit" on an existing scope — verify form is pre-populated

## CRUD Operations
### Add Scope
1. Click "Add Scope"
2. Fill in Name = "e2e:read", Display Name = "E2E Read Access"
3. Optionally add a description
4. Submit the form
5. Verify "e2e:read" appears in the scope list

### Edit Scope
1. Find "e2e:read"
2. Click "Edit"
3. Update the Display Name
4. Submit and verify

## Visual Checks
- Standard/system scopes should be visually distinguished (badge, icon, or different row color)
- Scope names should appear in monospace/code font for clarity
- Add/Edit form should have logical grouping: name, display name, description, claims
