# Page: Admin – Role List
## Route: /admin/roles

## Expectations
- A list/table of roles should be shown (at minimum: admin, user system roles)
- "Add Role" button must be visible
- Each role should have edit/delete actions

## Actions
- Verify at least one system role is present
- Click "Add Role" — verify the add form renders
- Navigate back
- Click "Edit" on an existing role — verify form is pre-populated

## CRUD Operations
### Add Role
1. Click "Add Role"
2. Fill in Name = "e2e-role", Display Name = "E2E Test Role"
3. Submit the form
4. Verify "e2e-role" appears in the role list

### Edit Role
1. Find "e2e-role"
2. Click "Edit"
3. Update the Display Name
4. Submit and verify

## Visual Checks
- System/built-in roles should have a visual indicator (lock icon, badge)
- Delete on system roles should be disabled or hidden
- Role names should use consistent typography with Scope page
