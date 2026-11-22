# Consent-Based User Assistance

Consent-based assistance lets a **tenant user ask a trusted colleague for temporary help**. The requester
stays the subject of every page and token, the helper acts as the requester only after accepting, and the
requester can end the session at any moment.

It is deliberately separate from the platform-admin features:

- [Real user impersonation](user-impersonation.md) is started by a platform administrator without the
  target user's consent and is writable.
- [Tenant support access](delegated-and-support-access.md#tenant-support-access-platform-admin) is the
  platform administrator's read-only path into a tenant.
- Assistance is started by the **requester**, accepted by an **invited tenant helper**, and remains inside
  the requester's tenant. Platform-admin control surfaces are never overlaid.

## At a glance

| Property | Value |
| --- | --- |
| Who can request | An active tenant user allowed by `requesterRoles`; empty means any active tenant user |
| Who can help | A tenant member holding a configured read-only or read-write helper role |
| Default helper role | `tenant-admin` for read-only; no read-write role by default |
| Default manager role | `tenant-admin` (can list and revoke every session in the tenant) |
| Feature flag | `Auth:EnableUserAssistance` (`AuthOptions.EnableUserAssistance`), default `false` |
| Tenant switch | `Assistance.Enabled`, default `true` when the platform flag is on |
| Requester UI | Account → Assistance (`/account/assistance`) |
| Helper UI | In-app inbox on the same page; email link also lands on `/account/assistance/accept` |
| Manager UI | `/admin/assistance` |
| Durable record | `UserAssistanceSessions` |
| Audit events | `user_assistance.requested`, `.accepted`, `.declined`, `.cancelled`, `.ended`, `.revoked`, `.expired`, `.token_denied`, `.validation_failed` |
| Elevated-context rule | Assistance, admin impersonation and tenant support access are mutually exclusive per user |

## Actor and subject model

Every assisted request keeps two identities:

- **Requester / subject** — the tenant user who asked for help and granted consent. Their user id is the
  token `sub`, and their effective roles and data are the maximum the helper can reach.
- **Helper / actor** — the invited tenant user who accepted. Their user-account id is `act.sub`, and every
  action is attributed to them in the audit trail.

The helper's authentication cookie and OP session stay intact. An overlay principal is installed only on
normal, non-control pages:

- `/account/assistance/*`, `/admin/assistance/*`, `/impersonation/*`, `/platform-admin/*`, login, logout,
  MFA, tenant selection, external login and end-session keep the helper's durable principal.
- The helper can therefore always see the inbox, end the session and sign out without being trapped in the
  requester's identity.

## Tenant settings

The normalized settings live in `TenantSettings.Assistance` and are exposed on **Admin → Settings**.

| Setting | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Tenant switch. The global `Auth:EnableUserAssistance` flag must also be on. |
| `rightsMode` | `intersection` | `intersection` = requester roles ∩ helper roles; `fullTarget` = all requester roles. |
| `requesterRoles` | empty | Role names allowed to request. Empty means any active tenant user. |
| `readOnlyHelperRoles` | `tenant-admin` | Role names that may accept read-only assistance. |
| `readWriteHelperRoles` | empty | Role names that may accept read-write assistance. A read-write role also implies read-only capability. |
| `managerRoles` | `tenant-admin` | Role names that can list and revoke all sessions in the tenant. |
| `readOnlyTokenPolicy` | `markerOnly` | `markerOnly` issues tokens with `assistance_mode=read_only`; `deny` blocks OIDC authorization for read-only sessions. |
| `defaultDurationMinutes` | `15` | Default session length offered to the requester. |
| `maxDurationMinutes` | `60` | Upper bound for a requested session. |
| `requestLifetimeMinutes` | `10` | How long an unanswered request stays pending. |

Role names are matched case-insensitively inside the requester's tenant. A role must exist before the
settings page will save it. Changing the configured role set changes eligibility for new requests and
acceptances; an active session whose helper loses every eligible role fails closed on the next request.

### Rights modes

- **Intersection (default).** The helper receives only role ids held by both the requester and the helper.
  The helper can never gain a role they do not already hold. If the intersection is empty, the session
  still permits authenticated base pages, but role-gated pages and role claims are absent.
- **Full target.** The helper receives the requester's full role set. This reproduces the requester's
  access exactly and is appropriate when the problem is user-specific.

Platform-realm roles are not mixed into tenant role claims, and platform-admin authorization handlers
reject every assistance principal.

## Lifecycle

1. The requester opens **Account → Assistance**, selects an eligible helper, chooses read-only or
   read-write, gives a reason, and may set a duration and ticket reference.
2. The service validates the feature flag, tenant switch, requester role, helper role, active users,
   self-request, duplicate pending request and elevated-context conflicts. It stores a `Pending` row with a
   one-time invitation token hash and sends an email. Email failure does not fail the request; the in-app
   inbox is primary.
3. The helper sees the request under **Help requested from me** and reviews it. Acceptance always requires
   the signed-in identity to match the invited user. When an email token is supplied, it must resolve to
   the same pending request; the link alone is never a bearer grant.
4. On acceptance the service re-checks the helper role, creates a dedicated OP session for the requester,
   marks the row `Active`, stores the effective role ids, clears the invitation token and binds the helper's
   browser session to the assistance session.
5. The overlay presents the requester's `sub`, name, email, tenant and effective roles. The helper remains
   `act.sub`. The requester sees a banner with the helper's name and can end the session at any time.
6. Ending — by the requester, the helper, a tenant manager, tenant disablement or expiry — marks the row in
   its terminal state, revokes the target OP session and every refresh/access token issued for it, and
   clears the browser session key. The requester's own OP session is untouched.
7. A background worker expires overdue pending requests and active sessions. Request paths also detect
   expiry and fail closed.

### Acceptance surfaces

- **In-app:** `/account/assistance` → **Review** → accept or decline.
- **Email:** `/account/assistance/accept?requestId=<id>&token=<one-time-token>`. The recipient must sign
  in as the invited identity. Any other signed-in user is refused, and the token is single-use because it
  is cleared on acceptance, decline, cancellation or expiry.

## Read-only and read-write

Read-only is enforced fail-closed on the OP's own surfaces:

- `GET`, `HEAD` and `OPTIONS` are allowed on overlaid paths.
- `POST`, `PUT`, `PATCH` and `DELETE` are rejected with `403` before endpoint code runs.
- Control paths remain available to the helper, including `/account/assistance/stop`.
- `/consent` is exempt so a marker-only token can be issued for a relying party; the token still advertises
  `assistance_mode=read_only`.

Read-write allows unsafe methods and real form submissions as the requester, subject to the effective role
set. It never unlocks platform-admin surfaces or another tenant.

### Token policy

With `readOnlyTokenPolicy=markerOnly`, read-only sessions can obtain authorization-code and refresh tokens.
Relying parties must inspect `assistance_mode` and refuse writes when it is `read_only`. Strict deployments
set `readOnlyTokenPolicy=deny`; `/authorize` then returns `access_denied` and token exchange returns
`invalid_grant` for read-only assistance, while OP-hosted pages remain usable.

## Issued token claims

Authorization-code access and ID tokens minted while assistance is active carry:

```json
{
  "sub": "<requester user id>",
  "name": "Alice Adams",
  "act": { "sub": "<helper user-account id>" },
  "assistance_id": "<UserAssistanceSession id>",
  "assistance_mode": "read_only",
  "amr": ["pwd", "assistance"]
}
```

Access tokens also carry an internal effective-role claim used by the OP's bearer-token authorization
handlers; it is not a general-purpose RP claim. When the `roles` scope is granted, the token's role claims
are filtered to the effective role set, so intersection mode cannot leak a requester-only role to the
helper.

`assistance_id` and `assistance_mode` are security metadata and are emitted regardless of scope or
claim-mapping restrictions. They distinguish consented assistance from admin impersonation's
`impersonation_id`.

## Manager view

Tenant managers (holders of `managerRoles`) see every assistance record in `/admin/assistance`: requester,
helper, mode, rights mode, status, creation time and deadline. Active sessions can be revoked with a
reason. Revocation is recorded as `Revoked` and emits `user_assistance.revoked`; the helper's access and
tokens are revoked immediately.

## Enabling it

The platform flag is off by default:

```yaml
Auth:
  EnableUserAssistance: true
```

Environment-variable form:

```bash
Auth__EnableUserAssistance=true
```

Then, in each tenant's **Admin → Settings → User Assistance**, confirm the switch and choose the requester,
helper and manager role names. The development compose stack and the E2E suite enable the platform flag
explicitly.

## Operational guidance

- **Start with read-only and intersection rights.** They are the safest defaults for support scenarios.
- **Use tenant-specific role names.** Do not assume a deployment uses `tenant-admin`; configure the roles
  that actually exist in the tenant.
- **Require a reason and, where possible, a ticket reference.** Both are stored on the durable row and
  emitted in audit events.
- **Retain `UserAssistanceSessions`.** It is the audit source for requester, helper, mode, reason, timestamps
  and the ending actor. Apply the deployment's normal audit-retention policy.
- **Watch the metrics.** `user_assistance.requests`, `.accepts`, `.cancels`, `.ends`, `.revocations`,
  `.expirations`, `.token_denials` and the `user_assistance.session_duration` histogram are exported from
  the `MrWhoOidc.WebAuth` meter.
- **Do not substitute assistance for impersonation.** Assistance requires consent and is requester-scoped;
  platform admins should keep using support access or impersonation for their own workflows.
- **Email is a notification, not a grant.** Keep the in-app inbox available even when SMTP is degraded.

## Difference from related features

| | Consent-based assistance | Real user impersonation | Tenant support access | Delegated access |
| --- | --- | --- | --- | --- |
| Started by | Requester (tenant user) | Platform admin | Platform admin | Delegator |
| Consent | Helper accepts; requester can revoke | None | Tenant admin grants support access | Delegate accepts the grant |
| Actor | Helper in `act.sub` | Platform admin in `act.sub` | Platform admin as request actor | Delegator |
| Subject / `sub` | Requester | Target tenant user | Empty tenant principal | Delegator |
| Writes | Read-only blocked; read-write per effective roles | Allowed as the target | Denied | Only delegated capabilities |
| Scope | One requester + one helper in one tenant | One tenant user | One tenant | One user + one client |
| Lifetime | Request-defined, 15 min default, 60 min maximum | 15 min default, 1–60 min | 15 min default, 1–60 min | Up to 7 days |

See [Delegated access and tenant support access](delegated-and-support-access.md) for the other models.
