# Page: Login
## Route: /login

## Expectations
- The login form must contain exactly two visible input fields: Username/Email and Password
- A submit button labelled "Sign in" or similar must be present
- The page title/heading should indicate this is a sign-in page
- Error messages must appear inline if credentials are wrong (not a separate page)
- The "Not you?" link should be visible if a pre-filled email is present

## Actions
- Verify the Username field accepts text input (placeholder: `alice or alice@example.com`)
- Verify the Password field is of type password (characters are masked)
- Submit the form with invalid credentials and verify an error alert appears
- Submit the form with valid credentials (`admin@default.local` / `Admin123!`) and verify redirect to home/dashboard

## CRUD Operations
- N/A — login is not a CRUD form, but we verify authentication outcome

## Visual Checks
- The login card should be centered on the page with appropriate maximum width
- Password field masking icon (if present) should align correctly
- Error alert styling: red/danger background, readable text, icon
- The heading font size should be noticeably larger than body text
- Sufficient contrast between form labels and background
- The submit button should be full-width and visually prominent
- Padding inside the card should prevent content from touching the edges
- No layout shift between normal state and error state (form should not reflow awkwardly)
- On a 1920×1080 layout, the card should not stretch to the full width (max-width constraint)
