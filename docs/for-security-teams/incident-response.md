# Security Incident Response

This is a product-specific response guide, not an established on-call service or a legal notification policy. Before deployment, assign an incident owner, backup contact, security contact, database operator, relying-party contacts, and the people authorized to approve containment and data recovery.

## Triage and Evidence

1. Record the incident identifier, UTC detection time, affected deployment/version, tenants, clients, and observed behavior. Separate confirmed facts from hypotheses.
2. For active unauthorized access or suspected signing-key compromise, engage the authorized incident team immediately. Set severity, update cadence, and escalation using your organization's policy.
3. Preserve relevant logs and state before restarting or rebuilding systems where feasible. Limit continuing harm; evidence collection must not postpone necessary containment.
4. Restrict compromised access paths and credentials. Consider blocking affected traffic at a trusted proxy while the scope is established; do not assume an undocumented application maintenance-mode switch exists.
5. Keep a timeline of operator actions and configuration changes. Store evidence in access-controlled storage; do not paste raw tokens, secrets, connection strings, or personal data into public tickets.

## Logs and Audit Data

For the source Compose stack, inspect container logs without assuming files exist under `/app/logs`:

```sh
docker compose ps
docker compose logs --since 1h --timestamps webauth
docker compose logs --since 1h --timestamps postgres
```

These commands can expose sensitive information. Use the deployment's controlled logging/export process for preservation, retention, and chain of custody. A terminal transcript alone is not a complete forensic record.

[AuthDbContext](../../MrWhoOidc.Auth/Persistence/AuthDbContext.cs) exposes `AuditEvents`, `TenantAuditLogs`, `RevocationAudits`, `ImpersonationAuditLogs`, and `ConfigurationAuditLogs`. Inspect the actual entity/schema and tenant scope before querying. There is no generic `AuthenticationEvents`/`TokenEvents` query supplied here, nor a promise that every protocol request is stored in an audit table. Correlate database events with application, proxy, and relying-party logs.

## Signing Key Compromise

Routine key rotation is not emergency revocation. Overlap windows intentionally keep older public keys available, and relying parties can cache JWKS.

1. Identify the compromised key identifier, affected issuers, exposure window, and relying parties. Protect evidence and restrict further unauthorized issuance.
2. Use the supported key-management workflow for the deployed version to establish new signing material. Do not edit encrypted key rows or change a `kid` manually as a substitute for replacing the key.
3. Coordinate rejection of the compromised key with relying parties, including their cached JWKS and offline JWT validation behavior. Removing a key from the issuer's JWKS does not instantly remove cached copies.
4. Review refresh tokens, sessions, and grants that may permit new issuance. Apply supported revocation controls and require reauthentication where appropriate.
5. Test that affected relying parties reject compromised tokens and accept newly issued ones before declaring containment complete.

An already issued self-contained JWT may remain accepted until expiry unless the resource server performs an online check or explicitly rejects the compromised key/token. Do not claim universal immediate revocation from a database update or logout alone.

## Client Secret Compromise

Revoke the affected credential through the authorized client-management workflow, coordinate replacement with the client owner, and inspect activity during the exposure window. Unlike routine overlap rotation, incident containment may require immediate revocation and temporary client downtime.

Credential rotation prevents future use of that credential; it does not by itself invalidate previously issued access tokens. Review refresh tokens, client grants, and resource-server validation. See the [client secret rotation guide](../for-operators/client-secret-rotation.md).

## TLS and DataProtection Material

For a compromised public TLS certificate, coordinate replacement and revocation with the issuing CA and hosting/proxy operator. Verify the full chain and client trust after deployment.

For DataProtection compromise, assess protected cookies, stored key material, and retained backups with the security owner. Replacing the encryption certificate does not undo prior exposure. Do not delete the key ring to force sign-out: stored signing keys and other protected data may depend on it. Plan containment and recovery with a tested copy and retain required decryption material securely.

## Recovery and Communication

- Patch or remove the confirmed access path, then validate recovery in isolation using [backup verification](../for-operators/backup-restore/verification-testing.md).
- Review whether restored state reintroduces revoked sessions, credentials, or grants before reopening traffic.
- Confirm issuer/JWKS, administrator access, a controlled token flow, tenant boundaries, and downstream rejection of revoked authority.
- Have the responsible legal/privacy team determine notification obligations and deadlines for the affected data and jurisdictions. This guide does not prescribe a universal notification period.
- Publish confirmed facts through approved channels, with an owner and next update time. Record unresolved exposure and validation gaps rather than declaring recovery from health checks alone.

## Preparedness Test

Exercise signing-key compromise, one client credential compromise, support-session revocation, and recovery from a protected database backup in a nonproduction environment. Record actual controls, downstream acceptance windows, owners, and verification results. Use [monitoring guidance](../for-operators/monitoring/alerting-rules.md) to validate alert delivery.

Reviewed 2026-09-05. This documentation review did not execute incident containment, token revocation, or recovery against a running deployment.
