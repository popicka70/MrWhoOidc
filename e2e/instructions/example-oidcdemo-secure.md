# Page: Example – OidcDemo Secure Page
## Route: example-oidcdemo-secure

## Expectations
- Heading "Secure Page" with a lock emoji, confirming the user authenticated through
  the identity server
- "Authenticated User Details" section: display name, email, subject, authentication
  method (scheme), and similar identity claims
- A tokens section where secret tokens are masked by default with reveal/copy controls
  and non-sensitive values are shown plainly
- The page must render its scripts section correctly (Show/Copy buttons functional)

## Actions
- Verify the heading and user details section render
- Verify the token table renders with masking
- Verify no server error text appears

## Visual Checks
- Consistent demo styling; no layout overflow with long token values
