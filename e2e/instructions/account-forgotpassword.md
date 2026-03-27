# Page: Forgot Password
## Route: /Account/ForgotPassword

## Expectations
- Page heading "Forgot Password" or "Reset Password" visible
- Email input field with label "Email Address" or similar
- Submit button "Send Reset Link" or "Request Password Reset"
- Cancel/Back-to-login link
- Success message after submitting (e.g., "Check your email for a reset link") — or inline message
- Info text: "Enter your registered email and we'll send you a password reset link"

## Actions
- Verify page loads without errors
- Verify email input field is visible and labeled
- Verify the Submit button is present and styled (btn-primary)
- Verify the back-to-login link exists

## Visual Checks
- Page uses auth-container or centered card layout (this is a public auth page)
- Email field uses form-control class and proper label
- Submit button prominent, centered or full-width in the card
- No admin sidebar visible (public page)
