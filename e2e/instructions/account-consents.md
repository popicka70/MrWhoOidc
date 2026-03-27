# Page: Account – Consents
## Route: /account/consents

## Expectations
- Page heading "My Consents" or "Authorized Applications" with a handshake or shield icon
- List or table of applications the user has granted consent to: App Name, Scopes Granted, Consent Date, Expiry
- "Revoke" button per consent entry
- Empty state if no consents granted: icon + "You have not granted consent to any applications yet."
- Informational text explaining what consents are (brief, e.g., "These are the applications you have authorized to access your account.")

## Actions
- Verify page loads without errors
- Verify any existing consent entries are shown with app name and granted scopes
- Verify Revoke button is present per entry
- Verify empty state message if no consents

## Visual Checks
- Scopes listed as small code or badge chips within each row
- Revoke button uses danger/outline-danger styling
- Empty state uses a relevant icon (ph ph-handshake or ph ph-shield-check)
- Page header uses Phosphor icon consistently with the rest of the account section
