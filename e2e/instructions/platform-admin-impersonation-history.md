# Page: Platform Admin – Impersonation History
## Route: /platform-admin/impersonation-history

## Expectations
- Page heading "Impersonation History" visible with a clock-history icon
- Table of impersonation events: Admin user, Impersonated user, Tenant, Reason, Start Time, End Time, Duration, Actions performed
- Filter controls: date range, admin user, impersonated user
- Pagination for long lists
- Empty state if no impersonations have occurred

## Actions
- Verify page loads without errors
- Verify table headers are present
- Verify filter form is submittable
- Verify empty state or data rows are shown

## Visual Checks
- Page header with ph ph-clock-counter-clockwise or ph ph-list-dashes icon
- Table uses data-table responsive class
- Each row clearly shows audit trail with all key fields
- Reason column should truncate long text with an ellipsis (tooltip on hover)
- Filter card above table matches standard card styling
