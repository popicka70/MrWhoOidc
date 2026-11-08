# Page: MFA / TOTP Setup
## Route: /mfa

## Expectations
- Page heading "Security" with the account tab navigation; active tab is Security
- Section heading "Two-factor authentication (TOTP)" with a concise explanation of TOTP
- The page shows ONE of three phases; each phase is a complete, valid state:

### Phase 1 — not enabled yet (the state most visits will see)
- A short explanatory sentence about authenticator apps
- An "Enable TOTP" primary button (btn-primary)
- No QR code yet: it appears only after enrollment is started

### Phase 2 — setup pending (after clicking Enable TOTP)
- QR code block (white, bordered) with the authenticator setup image
- Manual setup key shown as a monospace code
- "Verification code" input (autocomplete one-time-code) with validation message support
- "Confirm" (btn-success) and "Cancel setup" (btn-outline-secondary) buttons

### Phase 3 — enabled
- Success alert "TOTP is enabled for this account."
- Remaining recovery-codes count
- Current-code input required for sensitive actions
- "Disable TOTP" (btn-danger) and "Generate new recovery codes" (btn-outline-secondary) buttons

- After a fresh enable: recovery codes are displayed once in a warning alert with a monospace list and a save/continue acknowledgment

## Actions
- Verify the page loads without errors for the account's current phase
- Verify the phase-appropriate action button is present (Enable TOTP / Confirm / Disable TOTP)
- Verify status/result messages render inside alerts, not raw text

## Visual Checks
- Account tabs visible with Security active
- Clear phase-appropriate call-to-action button
- QR code (when present) inside a bordered white card
- Recovery codes (when present) in a monospace list inside an alert
