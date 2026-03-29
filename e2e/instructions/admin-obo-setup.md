# Page: Admin – On-Behalf-Of Setup
## Route: /admin/obo-setup

## Expectations
- Page heading "On-Behalf-Of Setup" or "OBO Policy" visible
- Table of OBO policy entries: Actor Client, Target Audience, Allowed Scopes, Status
- "Add OBO Policy" button to create new delegation rules
- Each row has Edit and Delete actions
- Informational text or link explaining what OBO (RFC 8693 token exchange) is
- Empty state if no policies are configured

## Actions
- Verify page loads without errors
- Check "Add OBO Policy" button is visible and navigates to the add form
- Verify the table renders correctly if policies exist

## Visual Checks
- Page header with Phosphor icon (ph ph-arrows-left-right or ph ph-flow-arrow)
- Policies listed clearly with client badges and scope chips
- Add button uses btn-success color
- Empty state uses a relevant icon (ph ph-arrows-left-right) and helpful text
