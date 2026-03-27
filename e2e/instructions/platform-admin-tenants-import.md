# Page: Platform Admin – Import Tenant
## Route: /platform-admin/tenants/import

## Expectations
- Page heading "Import Tenant" or "Bulk Import" with an upload/import icon
- File upload control for JSON tenant export file
- OR paste JSON textarea for importing tenant config directly
- "Validate" and "Apply Import" buttons (apply only enabled after validation)
- Validation results panel showing any errors or warnings from the import file
- Warning: "This will overwrite existing tenant configuration if slug matches"
- Cancel/Back link to return to tenant list

## Actions
- Verify page loads without errors
- Verify file upload or JSON paste area is visible
- Verify Validate and Import buttons exist
- Verify Cancel/Back link is present

## Visual Checks
- Page header with ph ph-upload or ph ph-file-arrow-up icon
- File upload area uses standard form-control file input or drag-and-drop area
- Apply/Import button uses btn-warning or btn-danger (destructive action)
- Validate button uses btn-primary
- Warning/confirmation text visible before import action
