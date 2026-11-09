# Page: Admin – Import Identity Provider
## Route: /admin/providers/import

## Expectations
- Page heading "Import Identity Provider" with an upload icon
- "Back to Providers" link
- An upload card with:
  - A JSON file upload control (accepts .json)
  - An "Or Paste JSON Content" monospace textarea
  - A Conflict Resolution select (Skip / Rename / Merge / Overwrite) with form-text
  - A "Preview Import" button that validates without applying
- If a preview ran: an Import Preview card with a color-coded header, validation
  errors, conflicts, and an execute button for valid previews
- Error/success flash banners when applicable

## Actions
- Verify page loads without errors
- Verify the file input and the JSON textarea are both present
- Verify all four conflict resolution strategies are listed
- Verify the "Preview Import" button is visible

## Visual Checks
- Upload form inside a card with a "Upload Configuration" header
- JSON textarea uses font-monospace
- This page is an import form, NOT a provider list: a table of providers, an
  "Add Provider" button, or Export controls are not expected here
