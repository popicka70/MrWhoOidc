# Security Keys and Passkeys

WebAuthn lets users authenticate with a compatible security key or platform authenticator. Availability depends on the browser, authenticator, secure origin, and deployment policy. This guide does not certify a list of device models or minimum browser versions.

## Register a Credential

1. Sign in using an allowed authentication method and open Account, then Security Keys. The current page route is `/account/web-authn`; follow tenant-aware navigation rather than reusing an old `/Auth/...` bookmark.
2. Select **Register New Security Key**, enter a recognizable **Key Name**, and complete the browser/authenticator prompt.
3. Confirm that the credential appears in the registered list. The page displays activity state and registration/last-used information when available.
4. Test sign-in in a separate browser session while retaining a working session or another approved recovery method. Register a backup authenticator where policy allows.

The authenticator's PIN or biometric interaction belongs to the browser/device prompt. Do not send it to administrators or support.

## Rename, Remove, and Recover

The Security Keys page provides **Rename** and **Remove** actions. Confirm which credential you are changing and retain another policy-approved way to sign in before removal.

If a device is lost, use a remaining approved method and remove the affected registration. If no usable method remains, contact the tenant's recovery administrator. This guide does not promise password fallback when policy requires WebAuthn, and administrators should not bypass policy with direct credential-table edits.

## Troubleshooting

- Confirm the public HTTPS origin and RP configuration before enrolling users. A hostname or RP-ID change can make existing credentials unusable for the new origin.
- Check browser permission, authenticator connection, and whether the user cancelled or timed out the prompt.
- A credential on one device may not be available on another; synchronization and cross-device support depend on the authenticator ecosystem.
- For a failed enrollment, verify the registered list before retrying so users can identify duplicate attempts.
- Capture the error and correlation context, not private authentication material, when asking for support.

## Operator Checks

Test enrollment, sign-in, rename, removal, recovery, and policy enforcement using the supported browser/device combinations for your organization. Test tenant navigation and changes to public origins before rollout. A passing page-load test does not verify a physical authenticator.

Source: [account page](../../MrWhoOidc.WebAuth/Pages/Account/WebAuthn.cshtml) and [page model](../../MrWhoOidc.WebAuth/Pages/Account/WebAuthn.cshtml.cs). The [older user guide](../done/webauthn-user-guide.md) is retained as historical context, not current device-support evidence.

Reviewed 2026-09-05. No browser or hardware-authenticator exercise was run during this documentation update.
