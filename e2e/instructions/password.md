# Page: Change Password
## Route: /password

## Expectations
- Page heading "Change Password" or "Update Password" with a lock icon
- Form fields: Current Password, New Password, Confirm New Password
- Password complexity rules displayed (e.g., "At least 8 characters, one uppercase, one number")
- Submit button "Change Password" or "Update Password" (btn-primary)
- Cancel/Back link to return to account
- Validation errors shown inline under fields if rules not met

## Actions
- Verify page loads without errors
- Verify all three password fields are present and labeled
- Verify password requirements text is visible
- Verify Submit button is present

## Visual Checks
- Page uses account layout (sidebar or nav visible, not auth-container)
- Password fields use form-control with type="password"
- Password strength indicator (optional) if present should be visible
- Submit button uses btn-primary
- Page header or breadcrumb shows "Change Password"
