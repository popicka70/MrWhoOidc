# Page: Admin – Import Realm
## Route: /admin/realms/import

## Expectations
- Page heading "Import Realm" with an upload icon
- Two input methods: a JSON file upload control AND a paste-JSON textarea
- Conflict resolution select (Skip / Rename / Merge / Overwrite) with explanatory form-text
- "Preview Import" button that validates the manifest without applying it
- A "Back to Realms" link to return to the realm list
- If a preview ran: a preview results card showing validation errors, detected conflicts,
  and obfuscated-secret warnings, plus an "Import Realm" execute button that only appears
  for a valid preview
- Error/success flash banners when applicable

## Actions
- Verify page loads without errors
- Verify the file input and the JSON textarea are both present
- Verify the conflict resolution select is present with all four strategies
- Verify the "Preview Import" button is visible
- Verify the "Back to Realms" link is present

## Visual Checks
- Page header with ph ph-upload icon
- Upload form inside a card with a "Upload Configuration" header
- File input styled with form-control, JSON textarea with font-monospace
- Preview results use color-coded headers (green when valid, red when invalid)
- This page is an import form, NOT a realm list: a table of realms or an "Add Realm"
  button is not expected here
