# Delegated Access and Tenant Support Access

This page covers two related features:

- **Delegated access:** one user lets another user act for them through a specific client.
- **Tenant support access:** a platform admin troubleshoots a tenant in a time-boxed session.

A third, more powerful feature — [real user impersonation](user-impersonation.md) — lets a platform admin
assume one tenant user's identity. It is opt-in, writable, and kept separate from the two models below.

Open defects for both are tracked in [the IdP assessment](../oidc-idp-assessment-2026-10-04.md), items R4 and R11–R12.

## Delegated access (user-to-user)

- **Off by default.** Set `Auth:EnableDelegatedAccess=true` to enable it (`AuthOptions.EnableDelegatedAccess`).
- **Delegable capabilities.** Only `profile.read` can be delegated, with a 7-day maximum grant lifetime. `profile.update_limited` is listed as a candidate but cannot be delegated (`Services/Delegation/IDelegableCapabilityCatalog.cs`).
- **Grant binding.** A grant is bound to one OAuth client. The delegate uses that grant through token exchange (RFC 8693), passing `delegation_id`.
- **Single hop only.** Exchanging a token that already carries `act` returns `invalid_grant` with the description `single_hop_only`.
- **Issued-token claims:**
  - `sub`: the delegator
  - `act.sub`: the delegate
  - `delegation_id`
  - `client_id` and `azp`: the caller
- **Resource servers.** They must introspect on every request so a revoked grant stops working immediately. `Examples/MrWhoOidc.TestApi` implements this at `GET /profiles/{id}/summary`.
- **Self-service UI.** Users manage grants at **Account → Delegated access**: *Granted by me* and *Delegated to me*. Revoking or declining a grant requires a reason.

## Tenant support access (platform admin)

- **On by default.** Controlled by `Auth:EnableTenantSupportAccess`.
- **Starting a session.** A platform admin starts a read-only session for one tenant and must give a reason; a ticket reference is optional.
- **Duration.** 15 minutes by default, clamped to 1–60 minutes (`TenantSupportAccessService`). An expiry job marks overdue sessions expired for all tenants.
- **Audit.** Start, stop and revoke emit `tenant_support_access.*` audit events.
- **Read-only enforcement.** Read-only access relies on each tenant-admin endpoint declaring a read or write operation. There is no automated check that every endpoint does (assessment R12).

## Comparison

| | Real user impersonation | Tenant support access | Delegated access |
| --- | --- | --- | --- |
| Enabled by | `Auth:EnableUserImpersonation` (default `false`) | `Auth:EnableTenantSupportAccess` (default `true`) | `Auth:EnableDelegatedAccess` (default `false`) |
| Actor | Platform admin in `act.sub` | Platform admin as the request actor | Delegator |
| Subject | Target tenant user | Empty tenant principal | Delegator |
| Writes | Allowed as the target user | Denied | Only delegated capabilities |
| Scope | One tenant user | One tenant | One user + one client |
| Lifetime | 1–60 minutes (15 default) | 1–60 minutes (15 default) | Up to 7 days for the grant |

Use [real user impersonation](user-impersonation.md) only when reproducing a problem requires the user's
permissions. Routine tenant troubleshooting should use read-only support access.
