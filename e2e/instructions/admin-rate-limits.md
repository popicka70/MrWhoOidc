# Page: Admin – Rate Limiting Dashboard
## Route: /admin/rate-limits

## Expectations
- Page heading "Rate Limiting Dashboard" visible with an icon (ph ph-gauge-fill)
- Overview stat cards: Total Requests (24h), Allowed Requests, Blocked Requests, Block Rate %
- "Rate Limiting Policies" table: Policy Name, Status (Active/Disabled), Requests (24h), Max/Window, Usage bar
- "Blocked IPs / Clients" table if any addresses are currently blocked
- "Refresh" button to reload live metrics
- Explanatory text: "Real-time monitoring of rate limiting across all OIDC endpoints"
- If no data available, an info alert "Loading rate limiting data..."

## Actions
- Verify page loads without errors
- Verify stat cards show numeric values (even if zeros)
- Verify the Policies table has at least one row (default policies are seeded)
- Click Refresh — page reloads and metrics update

## Visual Checks
- Stat cards should use Bootstrap contextual backgrounds: primary, success, danger, warning
- h1 heading (not h2) for the page title, consistent with other admin pages
- Phosphor icon in heading (ph ph-gauge-fill), NOT Bootstrap icon (bi bi-arrow-clockwise)
- Refresh button uses Phosphor icon ph-arrow-clockwise
- Policy usage shown as a progress bar with percentage
- Page uses `page-header` component matching other admin pages
