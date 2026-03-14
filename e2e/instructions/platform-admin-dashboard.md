# Page: Platform Admin – Dashboard
## Route: /PlatformAdmin

## Expectations
- A summary dashboard for the platform administrator must be displayed
- Key metrics: tenant count, user count, active sessions, or similar
- Navigation to Tenants, Impersonation, License, and Settings should be clear
- Platform admin section must only be visible to platform-admin role

## Actions
- Verify metrics cards or summary statistics are rendered
- Verify the Tenants link navigates to the tenant list
- Verify all quick-action links resolve correctly (no 404)

## Visual Checks
- Dashboard cards or metric tiles should be evenly spaced in a grid layout
- Metric values should be larger/bolder than labels
- The warning or feature banner (if active license required) should be clearly styled
- Navigation breadcrumb (if present) should reflect "Platform Admin > Dashboard"
- Color coding: metrics should use consistent accent colors, not random rainbow of colors
- The dashboard should feel distinct from the tenant admin area (e.g. different section color or label)
