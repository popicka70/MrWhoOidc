# Page: Account – Profile
## Route: /Account/Profile

## Expectations
- The profile edit form must show the logged-in user's current information
- Fields: First Name, Last Name, Display Name (or Username) should be editable
- A save/update button must be present
- Account section navigation tabs must be visible (Profile, Emails, WebAuthn, Sessions, Consents, Linked)

## Actions
- Verify the currently-logged-in email or username is shown
- Verify all editable fields are prefilled with existing values
- Update the Display Name field with a new value
- Submit the form and verify a success message appears
- Revert back to original value

## CRUD Operations
### Update Profile
1. Clear the Display Name field
2. Type "E2E Admin User"
3. Click Save/Update
4. Verify the page shows a success notification
5. Verify the displayed name has changed in the UI (e.g. in navbar)

## Visual Checks
- Tab navigation at the top (or side) of the account section should be clearly highlighted for "Profile"
- Form should have clear field labels above inputs (not placeholder-only)
- Submit button should be distinguishable from cancel/back links
- Success notification (toast or inline) should be visually distinct (green/success color)
- Avatar or profile image area (if present) should have upload affordance
