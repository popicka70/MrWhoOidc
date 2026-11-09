# Page: Registration (Public Sign-Up)
## Route: /Registrations

## Expectations
- A public sign-up / account request form
- Heading that clearly communicates creating a new account
- Fields appropriate to the configured registration mode: email and/or username,
  password and confirmation, display name
- A submit action ("Register" / "Create account")
- Links back to sign-in
- Inline validation feedback on invalid input; a success state (e.g. confirmation
  notice that the request awaits approval) after submission when applicable

## Actions
- Verify page loads without errors
- Verify the form fields and submit action are present
- Verify the sign-in link is available

## Visual Checks
- Centered auth-card layout consistent with the other public pages
- Password field masked
- This page is a public sign-up form: admin chrome, sidebars, and tenant admin
  tables are not expected
