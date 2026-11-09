# Page: Admin – Import Client
## Route: /admin/clients/import

## Expectations
- Page heading "Import Client" with an upload icon
- "Back to Clients" link to return to the client list
- An "Upload Configuration" card with:
  - A JSON file upload control (accepts .json)
  - An "Or Paste JSON Content" monospace textarea
  - A Conflict Resolution select (Skip / Rename / Merge / Overwrite) with form-text
  - A "Preview Import" button that validates without applying
- If a preview ran: an Import Preview card with color-coded header (green when valid,
  red when invalid), validation errors, detected conflicts, and an "Import Client"
  execute button for valid previews
- Error/success flash banners when applicable

## Actions
- Verify page loads without errors
- Verify the file input and the JSON textarea are both present
- Verify the conflict resolution select is present with all four strategies
- Verify the "Preview Import" button is visible
- Verify the "Back to Clients" link is present

## Visual Checks
- Upload form inside a card with a "Upload Configuration" header
- File input styled with form-control, JSON textarea with font-monospace
- Preview results use color-coded headers and list-group items
- This page is a client import form, NOT a client list: a table of clients,
  an "Add Client" button, or Export controls are not expected here
