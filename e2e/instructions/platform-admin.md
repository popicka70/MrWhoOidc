# Page: Platform Admin – Dashboard
## Route: /platform-admin

## Expectations
- A summary dashboard for the platform administrator
- Key metrics as stat cards: tenant count, user count, and similar platform-wide numbers
- "Quick Actions" card with a consistent, uniform button style across all four tiles:
  Seed Tenant (opens a modal), Manage Tenants, Unassigned Users, System Health
- A "Recent Tenants" table (name, status, created) with per-row Edit actions, plus a
  "View All" link to the full tenant list
- Multi-tenancy mode indicator where shown

## Actions
- Verify stat/metric cards render with values
- Verify quick-action tiles have uniform button styling
- Verify the Recent Tenants section renders

## Visual Checks
- Stat cards evenly spaced in a grid; values larger/bolder than labels
- All quick-action buttons share the same visual weight — no random color per tile
- The dashboard feels distinct from the tenant admin area
