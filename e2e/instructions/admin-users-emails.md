# Page: Admin – User Alternative Emails
## Route: /admin/users/emails

## Expectations
- Page heading "Alternative emails" for a single user
- The user-detail sub-tab strip with the Emails tab active
- A form to add another email address for the user
- A table/list of the user's existing alternative emails with their verification
  state, and remove actions when entries exist
- Friendly empty state when no alternative emails exist

## Actions
- Verify page loads without errors
- Verify the add-email form is present
- Verify the existing emails list or its empty state is visible

## Visual Checks
- Verification state shown as badges (verified=success, pending=warning)
- This page is a SINGLE user's email editor: the users list or user search
  are not expected here
