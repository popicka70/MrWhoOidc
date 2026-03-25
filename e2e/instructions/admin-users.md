# Page: Admin – User List
## Route: /admin/users

## Expectations
- A searchable table/list of user accounts should be present
- Each row must show at minimum: username/email, status, and action links
- "Add User" button visible and prominent
- Search/filter input visible

## Actions
- Verify at least one user exists (admin@mrwho.local seeded at startup)
- Click "Add User" — verify the add user form appears
- Navigate back
- Click "Edit" on the first user — verify the edit form opens
- Navigate to the sub-tabs: Clients, Emails, Linked Accounts, Roles — capture each

## CRUD Operations
### Add User
1. Click "Add User"
2. Fill in Username = "e2e-test-user", Email = "e2e@test.local", Password = "TestPass123!"
3. Submit the form
4. Verify the new user appears in the list

### Edit User
1. Find "e2e-test-user" (or admin@mrwho.local)
2. Click "Edit"
3. Update display name or another safe field
4. Submit and verify changes saved

## Visual Checks
- User status badges (active/inactive) should be visually distinct
- The edit and delete actions should have consistent styling with other admin pages
- Search input should have a magnifier icon and clear affordance
- Pagination controls (if many users) should be at page bottom
- Table responsive — no overflow at 1920×1080
