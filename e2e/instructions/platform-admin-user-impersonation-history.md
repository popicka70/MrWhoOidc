# Page: Platform Admin – User Impersonation History
## Route: /platform-admin/user-impersonation/history

## Expectations
- Page heading "Impersonation History" with a clock-history icon
- Summary cards for total sessions, active sessions and the number currently shown
- Filter form: actor username, target username, tenant slug, started-after and started-before dates, with
  Apply filters and Clear actions
- Table with Actor, Target user, Tenant, Reason, Started, Ended, Duration, Status and Actions columns
- Active rows are highlighted and offer a Revoke action with a required reason
- Empty state when no session matches the filters
- If the feature is disabled, a warning explains that `Auth:EnableUserImpersonation` must be set

## Actions
- Verify the page loads without errors
- Verify the table headers and filter controls are present
- Verify a completed session shows the actor, target, tenant, reason, ticket, duration and Ended status
- Verify an active session can be revoked with a reason and then shows Revoked
- Verify the legacy `/platform-admin/impersonation-history` route redirects here

## Visual Checks
- Page header uses a Phosphor clock-counter-clockwise icon (`ph ph-clock-counter-clockwise`)
- Table uses the shared data-table styling and is responsive
- Active sessions use a danger highlight; completed/revoked sessions use a neutral badge
- Long reasons remain readable without breaking the table layout
