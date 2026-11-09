# Page: Example – React OIDC Client (Authenticated Home)
## Route: example-reactclient-authenticated

## Expectations
- Dark-themed demo client, header "MrWho OIDC React Demo"
- When authenticated: the header shows a "Logout" button (NOT "Login"), plus a
  "Welcome, <name>" heading
- Two-column layout:
  - Left: "User Claims" card listing all claims with code-styled names and readable
    values; timestamp claims (exp, iat, ...) formatted as human-readable local time,
    without a raw epoch number in parentheses
  - Right: "Tokens" card with masked secret values (access/id token) with Copy
    buttons, plus token type and an "Expires in" duration in human units
    (e.g. "1 hour"), not raw "3600s"

## Actions
- Verify the header shows Logout when authenticated
- Verify the claims and tokens cards render
- Verify durations and timestamps are human-readable

## Visual Checks
- Balanced two-column layout
- Masked tokens include ellipsis; copy buttons give feedback
