# Page: Admin – Configuration Audit
## Route: /admin/configuration-audit

## Expectations
- Page heading "Configuration Audit" or "Audit Log" visible
- Filter controls: date range, action type, actor (user who made the change)
- Table of audit entries with columns: Timestamp, Actor, Action/Event, Entity Type, Entity, Details
- Timestamps displayed in local or UTC with consistent format
- Pagination for large audit logs
- If no entries match filters, a descriptive empty state (not just a blank table)
- "Export" button to download audit log as CSV or JSON

## Actions
- Verify page loads without errors
- Submit the filter form with default values — verify results shown or empty state message
- Change date range filter — verify table updates
- Verify at least one audit entry is shown (from prior admin operations)

## Visual Checks
- Filter card should appear above the result table
- Table rows should alternate or use hover highlight
- Timestamps should not overflow; use text-nowrap or minimum column width
- Action badges (Create/Update/Delete) should use color coding
- Empty state icon (ph ph-magnifying-glass or ph ph-clock-counter-clockwise) with description text
