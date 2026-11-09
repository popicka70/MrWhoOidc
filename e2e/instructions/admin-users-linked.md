# Page: Admin – User Linked Accounts
## Route: /admin/users/linked

## Expectations
- Page heading "Linked accounts" for a single user, with the user's display name as subtitle
- The user-detail sub-tab strip (Overview/Profile, Emails, Roles, Clients, Linked)
  with the Linked tab active
- Explanatory text: external identity providers linked to this user; removing a link
  prevents sign-in via that external account until re-established
- A table of linked external accounts: Provider, Issuer, Subject, Created, Last seen,
  and a Remove action per row — or a friendly empty state when nothing is linked

## Actions
- Verify page loads without errors
- Verify the sub-tab strip renders with the Linked tab highlighted
- Verify the linked accounts table or its empty state is visible

## Visual Checks
- Provider names use consistent styling
- This page is a SINGLE user's linked-account view: the users list, user search, or
  "Add User" are not expected here
