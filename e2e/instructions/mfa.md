# Page: MFA / TOTP Setup
## Route: /mfa

## Expectations
- Page heading "Multi-Factor Authentication" or "Two-Factor Authentication (2FA)" with a shield icon
- Current MFA status shown: Enabled/Disabled with visual badge
- If MFA not enabled: QR code setup section with "Scan this QR code with your authenticator app"
  - QR code image visible
  - Manual secret key shown as fallback (copy button)
  - Verification code input to confirm setup
  - "Enable MFA" / "Activate" button
- If MFA already enabled: "Disable MFA" button (btn-danger or btn-outline-danger) with confirmation
- Recovery codes section (show/regenerate after MFA is enabled)
- Supported apps: Google Authenticator, Authy, Microsoft Authenticator mentioned

## Actions
- Verify page loads without errors
- Verify MFA status is clearly shown (enabled or disabled)
- Verify QR code or setup section is visible if MFA is not enabled
- Verify relevant action button is present (Enable MFA or Disable MFA)

## Visual Checks
- Page header with ph ph-shield or ph ph-shield-check icon
- QR code rendered as an `<img>` or `<canvas>` element in a bordered card
- Manual secret key in a monospace code element with a copy button
- Verification input labeled "Authentication Code" or "OTP Code"
- Recovery codes in a bordered text block with copy-all button
