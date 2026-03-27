# Page: Admin – Registrations
## Route: /admin/registrations

## Expectations
- Page heading "Registrations" with a clipboard icon (ph ph-clipboard-text) and subtitle
- Table of registration requests: Email, Name, Client, State (Pending/Approved/Rejected), Created, Decision date
- Approve and Reject action buttons for Pending registrations
- Status filter dropdown or tabs (All / Pending / Approved / Rejected)
- Empty state with clipboard icon and message "No registrations found" if no data
- Tenant context banner if multi-tenancy is enabled

## Actions
- Verify page loads without errors
- Verify table headers are present even when empty
- Verify the empty state shows an icon and helpful text (not a blank table)
- If any pending registrations exist, verify Approve/Reject buttons are shown

## Visual Checks
- Page header should match the standard page-header component with Phosphor icon
- State badges: Pending=warning/yellow, Approved=success/green, Rejected=danger/red
- Approve button should be btn-success (green), Reject button btn-outline-danger
- Table uses `table-responsive-cards` for mobile responsiveness
