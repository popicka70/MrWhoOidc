# Page: Admin – License Management
## Route: /admin/license

## Expectations
- Page heading "License Management" with a medal or certificate icon (ph ph-medal)
- Current license info: License Type (Community/Enterprise/Trial), Status (Active/Expired), Expiry date, Seat count
- "Install License" or "Upload License Key" button
- License feature matrix: table or checklist of features enabled vs. disabled
- Warning banner if license is expiring within 30 days or already expired
- Link to documentation or license purchase page

## Actions
- Verify page loads without errors
- Verify license status is displayed clearly (Active/Expired/Trial)
- Verify the Install License button is visible

## Visual Checks
- Page header uses `page-header` component or d-flex pattern with Phosphor icon
- License status uses color-coded badge: success=Active, warning=Expiring, danger=Expired
- Feature matrix uses check/x icons (ph ph-check / ph ph-x) in a responsive table
- Any trial/demo banner should be prominent but not overwhelming
