# Page: Admin – Realm List
## Route: /admin/realms

## Expectations
- A list/table of realms should be present
- Default realm and admin realm should both be visible after seeding
- "Add Realm" button must be visible
- Each realm row must have edit/delete actions

## Actions
- Verify "default" and "admin" realms exist
- Click "Add Realm" — verify the add form appears
- Navigate back
- Click "Edit" on the "default" realm — verify the form is populated

## CRUD Operations
### Add Realm
1. Click "Add Realm"
2. Fill in Name = "e2e-realm", Display Name = "E2E Test Realm"
3. Submit the form
4. Verify "e2e-realm" appears in the list

### Edit Realm
1. Find "e2e-realm" in the list
2. Click "Edit"
3. Update the Display Name to "E2E Test Realm (Updated)"
4. Submit and verify the change is reflected

## Visual Checks
- Realm rows should visually distinguish "system" realms from user-created ones
- Delete action on system realms should either be absent or disabled
- The form fields for name/display name should have clear labels with validation hints
