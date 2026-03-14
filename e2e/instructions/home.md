# Page: Home / Landing
## Route: /

## Expectations
- The page title or heading should make clear this is an OIDC identity provider
- Navigation/sidebar should be visible with links to account, admin, and public pages
- Tenant context should be apparent (name, logo if configured)
- A clear call-to-action for logging in or account management
- Quick links to OIDC Discovery and JWKS endpoints should be accessible

## Actions
- Verify that the main heading or welcome message is present and readable
- Verify that the sidebar or navigation menu renders correctly
- Click the link to OIDC Discovery (`.well-known/openid-configuration`) and verify it opens
- Navigate back and verify the page is still intact

## Visual Checks
- Adequate whitespace and padding around content elements
- Consistent font sizes and weights
- No broken images, icons, or layout overflow
- Sidebar icons and labels aligned correctly
- Color scheme consistent with brand/identity theme
- Responsive layout — no horizontal scrollbars at 1920×1080
- Footer (if present) should be pinned to the bottom without overlap
