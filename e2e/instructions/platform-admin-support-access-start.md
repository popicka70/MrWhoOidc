# Page: Platform Admin – Start Support Access
## Route: /platform-admin/support-access/start

## Expectations
- After submitting the support access form, the page redirects to the tenant admin dashboard
- A support access banner is visible on the tenant admin page
- The banner shows: "Support Access" label, actor name, tenant name, "Read-only" mode, remaining time
- The banner does not suggest that a tenant user identity is assumed

## Actions
- Verify redirection to a tenant admin dashboard page (e.g., /admin/... or /tenants/...)
- Verify the support access banner is visible and contains:
  - "Support Access" text
  - The reason provided ("Troubleshooting")
  - Read-only indication

## Visual Checks
- Banner uses alert-warning or alert-info styling
- Banner is persistent (visible on the page)
- No text suggesting "impersonation" or "impersonate" is visible
