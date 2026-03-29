# Page: Platform Admin – Tenants
## Route: /platform-admin/tenants

## Expectations
- Page heading "Tenants" or "Manage Tenants" with a buildings icon
- Table of all tenants: Tenant Name, Slug, Status (Active/Disabled), User Count, Created date
- "Add Tenant" button (btn-success)
- "Import Tenant" button for bulk import
- Edit, Disable/Enable, and Delete actions per tenant row
- Search/filter field to find tenants by name or slug
- Platform admin sees ALL tenants across the system

## Actions
- Verify page loads without errors
- Verify at least one tenant is listed (the default tenant from seeding)
- Verify "Add Tenant" button is visible
- Verify "Import" button or link is visible
- Verify Edit action is present per row

## Visual Checks
- Page header uses page-header component with ph ph-buildings icon
- Table uses data-table class with responsive breakpoints
- Status badges: Active=success, Disabled=danger
- "Add Tenant" button uses btn-success
- User count shown as numeric or badge
