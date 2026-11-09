# Page: Admin – Add User
## Route: /admin/users/add

## Expectations
- Page heading "Add user" (or "Create user") with an icon
- Tenant context banner when applicable
- Form fields:
  - "Username" (required, marked with *)
  - "Email (optional)" with help text that email is needed for password reset /
    first-login flow — strongly recommended
  - "Name (display)"
- Save (primary, with a check icon) and Cancel actions

## Actions
- Verify page loads without errors
- Verify Username, Email, and Name controls are present
- Verify Save and Cancel actions are visible

## Visual Checks
- Required fields marked with a red asterisk
- This page is a creation form, NOT a user list: a table of users, search, or
  pagination are not expected here
