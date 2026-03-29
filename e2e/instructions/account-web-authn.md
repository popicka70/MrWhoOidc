# Page: Account – WebAuthn / Passkeys
## Route: /account/webauthn

## Expectations
- Page heading "Security Keys" or "Passkeys / WebAuthn" with a fingerprint or key icon
- Registered security keys listed: name, type (Platform/Cross-Platform), registered date
- "Register new key" button
- "Remove" button per key
- Empty state if no keys registered: friendly message + "Register your first security key" button
- Browser WebAuthn support notice or error if not supported

## Actions
- Verify page loads without errors
- Verify the "Register new key" / "Add passkey" button is visible
- Verify any existing registered keys are listed with their nicknames
- Verify remove button is present per key

## Visual Checks
- Page header with ph ph-fingerprint or ph ph-key icon
- Security key rows show a hardware key / platform icon
- Register button uses btn-primary or btn-success
- Empty state uses ph ph-key or ph ph-fingerprint icon with helpful text
