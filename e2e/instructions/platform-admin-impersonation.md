# Page: Platform Admin – Impersonation
## Route: /platform-admin/impersonation

## Expectations
- Page heading "Impersonation" or "User Impersonation" with a person-arrow or mask icon
- Form to start impersonation: User search (by email/username), Target tenant selector, Reason input (required for audit)
- "Start Impersonation" / "Impersonate" button (btn-danger or btn-warning to signal caution)
- Warning banner: "This action is audited. All actions performed while impersonating are logged."
- Link to impersonation history
- Validation: user must exist, reason must not be blank

## Actions
- Verify page loads without errors
- Verify the user search input is visible and functional
- Verify the reason/justification field is present and required
- Verify the Impersonate button is present with appropriate styling

## Visual Checks
- Warning banner is prominent (alert-warning or alert-danger)
- Page header uses Phosphor icon (ph ph-user-switch or ph ph-user-focus)
- Reason field labeled clearly (e.g., "Reason (required, will be audited)")
- Submit button uses a caution color (btn-warning or btn-danger) — not btn-primary
