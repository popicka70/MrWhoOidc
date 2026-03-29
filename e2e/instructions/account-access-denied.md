# Page: Account – Access Denied
## Route: /account/access-denied

## Expectations
- "Access Denied" heading clearly visible, ideally in a danger/red card header
- Brief explanation: "You don't have permission to access this resource"
- Bullet list of reasons: missing role, different tenant, expired session
- "Go to Home" button (btn-primary)
- "Go to Dashboard" button (btn-outline-secondary) if authenticated
- "My Account" button (btn-outline-secondary) if authenticated
- "Sign In" button (btn-success) if not authenticated
- Attempted URL shown if available
- "Need Help?" info block with admin contact suggestion

## Actions
- Verify page loads without errors
- Verify the heading and explanation are visible
- Verify at least one navigation button is present
- Verify the attempted URL is displayed (if present in the URL)

## Visual Checks
- Card should have a danger border (border-danger) and a bg-danger card header
- The card should not be excessively narrow; it should fill a reasonable width
- Navigation buttons spaced with gap-2 and flex-wrap for responsiveness
- "Need Help?" info block in bg-light rounded section at the bottom of the card
- The page should use the full admin/account layout (no auth-container centering trick)
