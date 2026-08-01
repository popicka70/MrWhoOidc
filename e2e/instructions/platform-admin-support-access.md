# Page: Platform Admin – Tenant Support Access
## Route: /platform-admin/support-access

## Expectations
- Page heading "Support Access" or "Tenant Support Access" with a person-arrow or mask icon
- Form to start support access: Target tenant selector, Reason input (required for audit)
- "Start Support Access" button (btn-danger or btn-warning to signal caution)
- Warning banner: "This action is audited. All actions performed during support access are logged."
- Link to support access history
- Validation: tenant must be active, reason must not be blank, expiry must be within policy bounds

## Actions
- Verify page loads without errors
- Verify the tenant selector is visible and functional
- Verify the reason/justification field is present and required
- Verify the Start Support Access button is present with appropriate styling

## Visual Checks
- Warning banner is prominent (alert-warning or alert-danger)
- Page header uses Phosphor icon (ph ph-user-switch or ph ph-user-focus)
- Reason field labeled clearly (e.g., "Reason (required, will be audited)")
- Submit button uses a caution color (btn-warning or btn-danger) — not btn-primary
