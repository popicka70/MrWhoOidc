# Page: Account – Profile
## Route: /Account/Profile

## Expectations
- The profile edit form must show the logged-in user's current information
- The product stores ONE display name per account: the "Full Name" field is editable and prefilled;
  first/last names are not separate entities, and the Username is shown read-only (immutable)
- Email field editable, with a warning that changing it requires re-verification
- A Save Changes button (primary) and a Cancel link must be present
- Account information card: tenant context, account status, registration date
- Account section navigation tabs must be visible (Dashboard, Profile, Password, Two-factor,
  Security Keys, Sessions, Consents, Linked, Emails) with Profile active

## Actions
- Verify the currently-logged-in email or username is shown
- Verify the Full Name field is prefilled with the existing value
- Update the Full Name field with a new value
- Submit the form and verify a success flash message appears

## CRUD Operations
### Update Profile
1. Type "E2E Admin User" into the Full Name field
2. Click Save Changes
3. Verify the page shows a success notification
4. Verify the displayed name has changed in the UI (e.g. in navbar)

## Visual Checks
- Tab navigation at the top (or side) of the account section should be clearly highlighted for "Profile"
- Form should have clear field labels above inputs (not placeholder-only)
- Submit button should be distinguishable from cancel/back links
- Success notification (toast or inline) should be visually distinct (green/success color)
- Avatar or profile image area (if present) should have upload affordance
