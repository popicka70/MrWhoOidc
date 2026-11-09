# Page: Platform Admin – Tenants
## Route: /platform-admin/tenants

## Expectations
- Page heading "Tenants" or "Manage Tenants" with a buildings icon
- Table of all tenants: Tenant Name, Slug, Status (Active/Suspended/Pending), User Count, Created date
- "Add Tenant" button (btn-success)
- "Import Tenants" button for bulk import
- Live search field (filters rows as you type) and a status filter dropdown
- Edit, Suspend/Enable, and Delete actions per tenant row — the default tenant is exempt
  (it cannot be suspended or deleted; it shows a Default badge and a disabled Suspend button)
- Delete is a soft delete: a dedicated confirmation page states that URLs stop resolving,
  data is retained and the slug stays reserved
- Platform admin sees ALL tenants across the system

## Actions
- Verify page loads without errors
- Verify at least one tenant is listed (the default tenant from seeding)
- Verify "Add Tenant" button is visible
- Verify "Import Tenants" button or link is visible
- Verify Edit action is present per row
- Verify the search input and status filter are visible

## Visual Checks
- Page header uses page-header component with ph ph-buildings icon
- Table uses data-table class with responsive breakpoints
- Status badges: Active=success, Suspended=warning, Pending setup=info
- "Add Tenant" button uses btn-success
- User count shown as numeric or badge
- Default tenant row shows a "Default" badge
