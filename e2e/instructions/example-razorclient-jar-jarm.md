# Page: Example – RazorClient JAR + JARM Dashboard
## Route: example-razorclient-jar-jarm

## Expectations
- Authenticated dashboard confirming the JAR (signed request object) + JARM
  (signed response) flow completed
- A tokens table where SECRET values (access_token, id_token, refresh_token) are
  masked with Show/Copy controls, and non-sensitive metadata (token_type,
  expires_in, scope) is shown in full without masking controls
- A claims table with claim names as code and values rendered readably
- No error state; sign-out control available

## Actions
- Verify the page loads without errors
- Verify masked values show an ellipsis and reveal correctly
- Verify non-sensitive metadata is displayed plainly

## Visual Checks
- Masked and unmasked values visually distinct
- No layout overflow with long token values
