# Page: Platform Admin – Settings
## Route: /platform-admin/settings

## Expectations
- Page heading "Platform Settings" with a gear/sliders icon
- Sections: General (instance name, contact email), Security (password policy, MFA enforcement), Registration (allow self-registration toggle), SMTP (email configuration), Feature Flags
- Save button per section or global Apply button
- Input validation feedback for required fields
- Any sensitive fields (SMTP password) masked with show/hide toggle

## Actions
- Verify page loads without errors
- Verify each settings section heading is visible
- Verify Save/Update button is present and styled

## Visual Checks
- Page header with Phosphor icon (ph ph-gear or ph ph-sliders-horizontal)
- Sections separated by card or hr dividers
- Toggle/switch controls for boolean settings
- Sensitive fields have an eye-toggle show/hide button
- Form uses standard Bootstrap mb-3 + form-label layout
