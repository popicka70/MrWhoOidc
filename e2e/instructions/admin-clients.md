# Page: Admin – Client List
## Route: /admin/clients

## Expectations
- A table or card list of registered OIDC clients should be present
- Each row/card must show at minimum: client name, client ID, and an actions column
- "Add Client" or "New Client" button must be visible and prominent
- Pagination or filtering controls should appear if many clients exist
- Export and Import buttons should be accessible

## Actions
- Verify at least one default client exists (seeded during startup)
- Click the "Add Client" button — verify navigation to the add client form
- Navigate back to the list
- Click "Edit" on the first client — verify the edit form opens with data pre-filled
- Navigate back to the list
- Verify the Export button is clickable

## CRUD Operations
### Add Client
1. Click "Add Client"
2. Fill in: Client Name = "E2E Test Client", Client ID = "e2e-test-client"
3. Select client type (Confidential or Public)
4. Submit the form
5. Verify the new client appears in the list

### Edit Client
1. Find "E2E Test Client" in the list (or the first client)
2. Click "Edit"
3. Change the Client Name to "E2E Test Client (Updated)"
4. Submit the form
5. Verify the updated name appears in the list

## Visual Checks
- Table columns should be evenly spaced and headers bold
- Action buttons (Edit, Delete) in each row should be compact and aligned
- The "Add Client" button should use a primary/accent color to draw attention
- Empty state (if no clients) should display a friendly message, not a blank area
- Long client IDs should truncate with an ellipsis, not overflow their cell
- Export/Import buttons should be visually secondary (not competing with "Add")
