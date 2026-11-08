# Page: Platform Admin – Import Tenant
## Route: /platform-admin/tenants/import

## Expectations
- Page heading "Import Tenant Configuration" or "Import Tenant" with an upload/import icon
- File upload control for JSON tenant export file
- OR paste JSON textarea for importing tenant config directly
- "Preview Import" button (primary) that validates the manifest, and an "Execute Import"
  button that appears only after a successful preview
- Conflict resolution select (Skip / Rename / Merge / Overwrite); the Overwrite option is
  labeled as destructive
- Destructive-mode warning banner appears when "Overwrite" is selected — live under the
  select and again next to the Execute button, including the number of conflicting entities
- Preview results panel showing validation errors, detected conflicts and manifest counts
- Dry Run checkbox to validate without applying
- Cancel/Back link to return to tenant list

## Actions
- Verify page loads without errors
- Verify file upload or JSON paste area is visible
- Verify Preview Import button is visible and primary-styled
- Verify Cancel/Back link is present
- Select the Overwrite conflict mode — verify the destructive warning appears

## Visual Checks
- Page header with ph ph-upload or ph ph-file-arrow-up icon
- File upload area uses standard form-control file input or drag-and-drop area
- Execute Import uses btn-danger when Overwrite is selected, btn-primary otherwise
- Preview Import button uses btn-primary
- Warning/confirmation text visible before import action
