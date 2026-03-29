# Page: Account – Email Addresses
## Route: /account/emails

## Expectations
- Page heading "Email Addresses" with an envelope icon
- List of email addresses linked to the account: address, Primary badge for primary email, Verified/Unverified status badge
- "Add Email" button to add a new email address
- "Make Primary" button for non-primary emails
- "Remove" button for non-primary emails
- Verification badge with "Resend Verification" link for unverified emails
- At least the current login email is shown

## Actions
- Verify page loads without errors
- Verify the primary email is shown with a Primary badge
- Verify "Add Email" button is present
- Verify verification badges are correct

## Visual Checks
- Primary email badge uses text-bg-primary or text-bg-success
- Verified badge: text-bg-success; Unverified: text-bg-warning
- "Add Email" button uses btn-success (green) color
- Page header with ph ph-envelope icon and subtitle
