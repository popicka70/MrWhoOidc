# Page: Admin – User Client Assignments
## Route: /admin/users/clients

## Expectations
- Page heading "Client assignments" for a single user
- The user-detail sub-tab strip (Overview/Profile, Emails, Roles, Clients, Linked)
  with the Clients tab active
- A form or list to assign/unassign the user to clients within the tenant
- Prefilled current assignments when they exist
- Friendly empty state when the user has no client assignments yet

## Actions
- Verify page loads without errors
- Verify the sub-tab strip renders with the Clients tab highlighted
- Verify the assignment form/list or its empty state is visible

## Visual Checks
- Client names rendered consistently (code or badge styling)
- This page is a SINGLE user's assignment editor: the users list, an "Add User"
  button, or user table pagination are not expected here
