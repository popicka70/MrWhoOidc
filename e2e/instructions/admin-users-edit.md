# Page: Admin – Edit User
## Route: /admin/users/edit

## Expectations
- Page header "Edit user" with the target user's name/email as subtitle
- Tenant context banner ("Tenant: <name>") when applicable
- The user-detail sub-tab strip (Overview/Profile, Emails, Roles, Clients, Linked)
  with the edit/profile tab active
- Edit form prefilled with the user's current values: Username, Email, display Name
- Save (primary) and Cancel actions

## Actions
- Verify page loads without errors
- Verify the form is prefilled
- Verify the sub-tab strip renders with the edit tab highlighted

## Visual Checks
- The sub-tab strip is the standard user tabs component
- This page edits ONE user: the searchable users list, an "Add User" button, or user
  table pagination are not expected here
