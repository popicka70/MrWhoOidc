# Client Secret Rotation

Use this procedure for confidential clients authenticated with a shared secret. Public clients and clients using another authentication method need their own credential workflow. Work in the intended tenant and client; record the operator and client owner.

## Current Controls

The client's Secrets tab supports creation, activation, Primary selection, and revocation. The [page handlers](../../MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml.cs) use [ClientStore](../../MrWhoOidc.Auth/Services/ClientStore.cs) and invalidate client caches after successful UI changes.

- A newly generated plaintext value is shown once; the stored credential is hashed. Deliver it through the client's secret manager or another approved secure channel.
- Creation can activate immediately or leave the secret inactive. Check its activation and expiry state before deploying it.
- The UI creation path refuses creation when it counts three active secrets. Do not treat that UI check as proof of a universal concurrency-safe limit across all writers.
- The UI accepts an expiry between 1 and 730 days when supplied; inspect the effective value rather than assuming every secret expires after 90 days.
- Primary is an administrative preference, not exclusive authentication eligibility. Old active secrets must be revoked explicitly.
- The store rejects revocation when no other activated, unrevoked secret exists. Its guard is not proof that the remaining secret is usable or unexpired.

## Routine Rotation

1. Inventory all client replicas/jobs using the old credential and confirm an owner, deployment route, and rollback plan.
2. Create a replacement in the client's Secrets tab with an appropriate description and expiry. Retain the old credential during the planned overlap.
3. Activate the replacement if it was created inactive. Transfer it into the client's secret storage without putting it into logs, tickets, or shell history.
4. Deploy the replacement to every consumer. Exercise the client's actual token flow and verify successful authentication with the new credential.
5. Mark the replacement Primary if useful to operators. Confirm that old consumers are no longer using the previous secret before revoking it.
6. Revoke the old secret and verify both that the new credential succeeds and that the old credential is rejected. Use a controlled client test; redact request credentials and token responses.
7. Record the secret identifiers, activation/revocation dates, expiry, client rollout evidence, and next review date. Do not record plaintext values.

Overlap reduces interruption only when every client deployment and authentication path has been verified. No fixed overlap duration is guaranteed by this guide.

## Failed Rotation or Compromise

For a failed rollout, keep the old secret only if it is still trusted and valid. Fix the deployment before revoking it; do not restore an exposed secret merely to restore availability.

For suspected compromise, use [incident response](../for-security-teams/incident-response.md). Immediate containment may require restricting the client or token endpoint access while an authorized replacement is activated. Do not bypass the last-secret revocation guard with direct database writes. Rotation does not invalidate already issued JWTs; check resource-server acceptance and refresh-token/grant state separately.

## Monitoring

Review expiry and authentication outcomes using the deployment's configured logging and [ClientSecretMetrics](../../MrWhoOidc.Auth/Observability/ClientSecretMetrics.cs). Test the exported metric names and alert delivery before relying on them. Do not use SQL or metric names from the historical playbook without checking the current schema and exporter.

## Historical Context

The original [user guide](../done/client-secret-rotation-guide.md) and [playbook](../done/client-secret-rotation-playbook.md) remain historical design records. This procedure is the current operational entry point.

Reviewed against the UI and store on 2026-09-05; no live credentials were created or revoked.
