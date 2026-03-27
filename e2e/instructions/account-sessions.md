# Page: Account – Active Sessions
## Route: /account/sessions

## Expectations
- Page heading "Active Sessions" or "Sessions" with a monitor/device icon
- List or table of active sessions: Device/Browser, IP Address, Last Active, Login Date, Current session highlighted
- "Revoke" button on each session (disabled or hidden for current session)
- "Revoke All Other Sessions" bulk action button
- Current session clearly labeled (e.g., "(This session)" badge)
- Empty state if only one session (current) or all revoked

## Actions
- Verify page loads without errors
- Verify the current session is highlighted or labeled
- Verify at least one session (the current one from the test) is listed
- Verify Revoke button is present for non-current sessions

## Visual Checks
- Page header with Phosphor icon (ph ph-monitor or ph ph-devices)
- Each session shows device/browser icon and geo info if available
- Current session uses a success/primary badge "Current"
- Revoke button uses btn-outline-danger (not btn-danger, to avoid accidental clicks)
- "Revoke All Other Sessions" uses a contextual warning or danger style
