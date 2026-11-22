# Page: Platform Admin – User Impersonation
## Route: /platform-admin/user-impersonation

## Expectations
- Page heading "User Impersonation" with a person-switch icon
- Warning that the administrator will act as the selected user; every action is audited and the session is
  short-lived
- Tenant selector (in multi-tenant mode), user search field (username, email or display name) and Search button
- Search results table with User, Email, Status and Action columns; only active users offer a Select button
- Start form: selected-user label, duration in minutes (1–60, default 15), optional ticket reference, required
  reason, and a danger-coloured "Start impersonation" button that stays disabled until a user is selected
- Link to "Impersonation History"
- If a session is active, a red active-session card names the target, tenant, actor, reason, ticket and
  remaining time, and offers "End impersonation"
- If the feature is disabled, a warning explains that `Auth:EnableUserImpersonation` must be set

## Actions
- Verify the page loads without errors and the search returns the seeded `alice` user
- Verify Select fills the selected-user label and enables the start button
- Verify a blank reason does not start a session
- Verify starting redirects to the target tenant account page and shows the impersonation banner
- Verify the active-session card appears when returning to this page
- Verify End impersonation returns to this page with a success message
- Verify the legacy `/platform-admin/impersonation` route redirects here

## Visual Checks
- Warning is prominent (alert-warning or alert-danger)
- Start and End buttons use a danger colour (btn-danger), never btn-primary
- Page header uses a Phosphor person-switch icon (`ph ph-user-switch`)
- Banner and active-session card make the target, actor, reason and remaining time immediately visible
- The target user's identity is shown, not the administrator's, after starting
