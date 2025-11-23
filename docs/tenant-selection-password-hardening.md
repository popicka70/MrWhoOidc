# Tenant Selection Password Hardening (2025-11-23)

## Background

The previous email-first login flow showed the list of tenant memberships as soon as the user entered a valid email address. Although the tenant list came from server-side lookups, it was rendered before the user proved they knew the account password, leaking organization names to anyone who knew (or could guess) a valid email.

## Changes

- Added `ITenantCredentialVerifier` in `MrWhoOidc.Auth` to validate email/password pairs across all tenant user records, supporting both Argon2id and BCrypt hashes.
- Updated `/select-tenant` to require a short-lived verification ticket before it hydrates tenant data from session. Anonymous users now see a password prompt first; the tenant cards only render after successful verification.
- Verification tickets are scoped to the hashed email address, expire after five minutes, and are cleared as soon as the user completes tenant selection.
- The UI still pre-fills the chosen tenant's login URL with the email, but users must re-authenticate again on the tenant-specific login (future work can reuse the credential ticket to skip the second password prompt).

## Rollout Notes

1. The password gate only applies to anonymous users arriving from `/discover-tenant`. Already authenticated admins keep the existing quick-switch experience.
2. Because verification happens before tenant data is read from session, empty/expired sessions still redirect back to `/discover-tenant` with the same error copy as before.
3. Tests: `TenantCredentialVerifierTests` cover the new verifier, and existing multi-tenancy tests continue to validate the tenant switching experience.

## Follow Up

- Evaluate reusing the verification ticket to auto-complete the tenant login (to avoid double password entry) once the user-account decoupling work lands.
- Consider surfacing an audit event when verification fails repeatedly so we can hook into the existing rate-limit telemetry.
