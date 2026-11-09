# Page: Admin – Provider Claim Mappings
## Route: /admin/providers/claim-mappings

## Expectations
- Page heading "Claim mappings – <provider name>"
- A "Back to provider" link and an "Export provider configuration" link near the heading
- An intro note explaining that mappings turn the claims an external provider sends into
  the claims this platform issues in its tokens
- An add-mapping form (card) with four inputs: External claim, Local claim,
  Transform (optional, with transform help text), and Order — plus an Add button
- A mappings table (Order, External, Local, Transform, Delete action) when mappings exist
- A friendly empty state (icon + guidance) when no mappings exist yet

## Actions
- Verify page loads without errors
- Verify the add-mapping form is present with all four fields
- Verify the back link to the provider details page
- Verify the empty state or the mapping table is rendered

## Visual Checks
- Form uses responsive row/col layout with labels above inputs
- Claim names rendered as <code> in the table, transform shown as code or an em-dash
  placeholder when empty
- Delete buttons are compact (btn-sm btn-outline-danger)
- This page manages ONE provider's claim mappings: the provider list table, an
  "Add Provider" button, or Export/Import controls for providers are NOT expected here
