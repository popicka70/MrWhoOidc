# Page: Example – RazorClient Secure (OBO)
## Route: example-razorclient-secure

## Expectations
- Heading "Secure area" with text confirming authentication through MrWhoOidc.WebAuth
- "Downstream API Call (On-Behalf-Of)" section with a table:
  - Subject, Name, Email, Audience, Delegating Client, Scopes, Issued, Expires
- Timestamp claims (iat/exp) rendered in a human-friendly format (e.g. "2026-01-01
  12:00:00 UTC"), not raw Unix epoch numbers
- Empty/absent values shown with a placeholder (em-dash), not blank cells
- An alert (warning) instead of the table when the downstream API call fails
- A sign-out affordance in the demo layout

## Actions
- Verify the heading and OBO section render
- Verify the table has all rows populated or explicit placeholders
- Verify timestamps are human-readable

## Visual Checks
- Table styling consistent with the demo design system
- Monospace styling for identifiers (subject, scopes)
