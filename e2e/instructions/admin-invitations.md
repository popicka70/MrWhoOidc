# Page: Admin – Tenant Invitations
## Route: /admin/invitations

## Expectations
- Page header "Invitations" with subtitle "Invite users to join <tenant>"
- When an invitation was just created: an "Invitation link" info banner with a
  readonly, selectable input containing the full link
- An invite form: email input and send/create invitation action
- A table of existing invitations: Email, status (pending/accepted/expired),
  created date, resend/revoke actions as applicable
- Friendly empty state when no invitations exist yet

## Actions
- Verify page loads without errors
- Verify the invite form is present
- Verify the invitations table or its empty state is visible

## Visual Checks
- Invitation link input is readonly with user-select-all styling
- Status values use badges with color coding
