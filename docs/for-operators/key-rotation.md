# Key Rotation and Certificate Lifecycles

Keep three purposes separate: server keys sign issued tokens; provider/client keys can sign requests to an upstream IdP; DataProtection certificates encrypt protected application data. Public TLS certificates secure transport and have a separate renewal process.

## Server Signing Keys

[KeyRotationOptions](../../MrWhoOidc.Auth/Services/KeyRotationOptions.cs) defines these code defaults. Configuration can override them; inspect the effective deployment values:

```json
{
  "KeyRotation": {
    "Enabled": true,
    "RsaKeySizeBits": 3072,
    "SigningAlgorithm": "RS256",
    "RotationInterval": "7.00:00:00",
    "Overlap": "2.00:00:00",
    "CheckPeriod": "01:00:00"
  }
}
```

For direct environment configuration, use keys such as `KeyRotation__RsaKeySizeBits`. With Compose, add explicit environment mappings; arbitrary `.env` names are not automatically passed to WebAuth.

[KeyRotationService](../../MrWhoOidc.Auth/Services/KeyRotationService.cs) rotates tenant signing keys when the current key reaches the configured age or when its RSA size is below the configured size. It invalidates active-key and public-JWKS caches after rotation. Changing the algorithm alone is not an immediate-rotation trigger in this service.

Retirement is calculated from each key's creation time using `RotationInterval + Overlap`, not from the exact replacement time. Check the effective publication window against token lifetimes, clock skew, delayed deliveries, and relying-party JWKS caches. Do not assume an arbitrary increase in overlap or a key-size change is risk-free.

## Routine Verification

1. Record the issuer, current public `kid` values, algorithm, configuration, and relying parties. Obtain JWKS from the tenant discovery document's `jwks_uri`, not from a guessed provider-key URL.
2. Confirm that relying parties accept the intended algorithm and can refresh keys. Test the change in an isolated deployment with representative clients.
3. Apply the reviewed configuration and monitor rotation logs. Do not delete key rows or edit encrypted JWKs to force rotation.
4. Confirm that new tokens use the expected key and validate at relying parties. Verify that still-valid older tokens remain accepted during the intended overlap.
5. After retirement, confirm JWKS publication and downstream behavior. Keep audit evidence with key identifiers, not private key material or raw tokens.

Automatic rotation does not establish emergency revocation. For compromise, coordinate downstream rejection and cached-key handling using [incident response](../for-security-teams/incident-response.md).

## Keys for Upstream JAR

The historical [provider key playbook](../done/key-rotation-playbook.md) is about outbound JAR, not server token signing. Do not use its timeline, endpoint assumptions, or online JWK-conversion suggestion as a current runbook.

For an upstream integration, identify the configured signing credential and the upstream client's verification keys. Confirm the deployed admin controls and the upstream's cache/manual-registration requirements. Publish or register the replacement public key before switching signing, verify an actual upstream login, and retain the old public key for the required validation window. Never upload private keys to an online conversion service.

See the [admin guide](../admin-guide.md) and [JAR/JARM guide](../jar-jarm-guide.md) for the integration context. A fixed server rotation setting does not rotate every external provider credential.

## DataProtection and TLS

Preserve DataProtection certificates and passwords required by existing key-ring entries and retained backups. Replacing a certificate is not the same as re-encrypting all historical protected data. Test decryption and recovery before retiring old material; keep it separately protected from database backups.

Renew public TLS certificates through the issuing CA and hosting/proxy workflow, checking SANs, chain, trust, and reload behavior. Do not reuse local `changeit` certificates in production. See [certificate configuration](../deployment-guide.md#tls-certificates) and [recovery verification](backup-restore/verification-testing.md).

Reviewed 2026-09-05. This review checked server rotation code; it did not perform a live rotation or certify an upstream provider's key-management workflow.
