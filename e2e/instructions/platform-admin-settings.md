# Page: Platform Admin – Settings
## Route: /platform-admin/settings

## Expectations
- Page heading "Platform Settings" with a gear/sliders icon
- Section navigation (pills) with anchors: General, Feature Flags, Security, Email (SMTP)
- General section: root sign-in behavior select, QR Login at Discovery toggle
- Feature Flags section: Token Exchange (RFC 8693) toggle, Dynamic Client Registration toggle
- Security section: Initial Access Tokens management (create with description, list active tokens, revoke)
- Email (SMTP) section: effective deployment configuration shown read-only (delivery enabled, relay host:port, TLS, auth mode, from address) plus a "Send test email" form with a recipient field
- A Save button for the persisted settings (General + Feature Flags)
- Note that SMTP values come from deployment configuration and are not stored in the database

## Actions
- Verify page loads without errors
- Verify each section heading and its anchor link in the nav are visible
- Verify the test email form (recipient input + submit) is present
- Verify Save button is present and styled (btn-primary)

## Visual Checks
- Page header with Phosphor icon (ph ph-sliders or ph ph-sliders-horizontal)
- Sections separated by titled headings and cards; anchor nav uses nav-pills
- Toggle/switch controls for boolean feature flags
- Form uses standard Bootstrap mb-3 + form-label layout
