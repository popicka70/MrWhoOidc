# Page: Admin – Branding
## Route: /admin/branding

## Expectations
- Page heading "Branding" or "Tenant Branding" visible, with a palette icon
- Form fields: Logo URL, Login page title, Primary color (color picker), Custom CSS, Favicon URL
- Live preview or preview button to see changes before saving
- Save/Update button, possibly Cancel/Reset to defaults
- Legal text fields: Terms of Service URL, Privacy Policy URL
- Help text for each field explaining the effect

## Actions
- Verify page loads without errors
- Verify all branding form fields are visible and editable
- Verify Save button is present and style is correct (btn-primary)

## Visual Checks
- Color picker input should be visible as an `<input type="color">` or custom color swatch
- Logo preview should show current logo or placeholder if not set
- Form should use standard Bootstrap form layout (form-label, form-control, mb-3)
- Page header with Phosphor icon (ph ph-palette or ph ph-paint-brush)
