# Page: Admin – Back-Channel Logout
## Route: /admin/backchannel

## Expectations
- Page title "Back-Channel Logout" or similar heading visible
- Table or list of back-channel logout queue entries (outbox records)
- Columns: Client ID, Logout Token type, Status (Pending/Sent/Failed), Attempt count, Created, Next Retry
- Admin controls: "Retry Failed" or "Clear Sent" bulk action buttons
- Summary metrics (total queued, failed, sent) shown as stat cards or alert banners
- Filter or status dropdown to narrow by Pending / Sent / Failed
- If queue is empty, a friendly empty state icon and message rather than a blank table

## Actions
- Verify the page loads without errors
- Check that status filters work (Pending, Sent, Failed)
- Verify bulk retry action is present and clickable
- Confirm any failure entries show the retry attempt count and last error

## Visual Checks
- Status badges should use color coding: success=green, pending=blue/yellow, failed=red
- Table should use the standard `data-table` class with responsive breakpoints
- Page header should use the standard `page-header` component with an icon (ph ph-funnel or ph ph-bell-ringing)
- Empty state should show a clipboard or bell icon with explanatory text
