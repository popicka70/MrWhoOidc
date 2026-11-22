# Real User Impersonation

Real user impersonation lets a **platform administrator** briefly assume one **tenant user's** identity. The
relying party receives that user's subject, name, roles and OP session, so the administrator can reproduce a
problem with the same permissions and data the user sees.

This is deliberately different from [tenant support access](delegated-and-support-access.md#tenant-support-access-platform-admin),
which keeps the administrator as the actor and is read-only. Impersonation is **writable** and is treated as the
target user's own session.

## At a glance

| Property | Value |
| --- | --- |
| Who can start it | A platform administrator (`platform-admin` policy) |
| Who can be impersonated | One active user in an active tenant |
| Default duration | 15 minutes, clamped to 1–60 minutes |
| Feature flag | `Auth:EnableUserImpersonation` (`AuthOptions.EnableUserImpersonation`), default `false` |
| Start / control UI | `/platform-admin/user-impersonation` |
| History UI | `/platform-admin/user-impersonation/history` |
| Stop endpoint | `/impersonation/stop` (POST) |
| Durable record | `UserImpersonationSessions` |
| Audit events | `user_impersonation.started`, `.ended`, `.revoked`, `.expired`, `.validation_failed` |

## Actor and subject model

Every impersonated request keeps two identities:

- **Actor** — the platform administrator. The durable session pins the actor's user id, user-account id and
  username. The actor's authentication cookie and OP session are never replaced.
- **Subject** — the target tenant user. The impersonation middleware installs an overlay principal built from
  the target's current claims, including the target's `sub`, tenant, roles and a dedicated target OP session.

The overlay is installed only after successful authentication and only on normal, non-control pages. The
platform administrator's own OP session remains tenant-bound to the administrator's tenant; the cookie validator
allows that cookie to be presented on the target tenant while a valid impersonation session names the
administrator as its actor.

Control surfaces are **never** overlaid: `/platform-admin/*`, `/impersonation/*`, login, logout, MFA, tenant
selection and the OIDC authorize/end-session endpoints keep the administrator's principal. This prevents
impersonation from being used to change the administrator's own credentials, tenants or sessions.

## Issued token claims

Tokens minted from an impersonated OP session carry the target identity plus explicit security metadata:

```json
{
  "sub": "<target user id>",
  "name": "Alice Adams",
  "act": { "sub": "<actor user-account id>" },
  "impersonation_id": "<UserImpersonationSession id>",
  "amr": ["pwd", "impersonation"]
}
```

`act` and `impersonation_id` are security metadata and are emitted regardless of scope or claim-mapping
restrictions. Resource servers and relying parties should log both values, and should treat the session as the
target user's own session for authorization decisions.

## Lifecycle

1. The administrator opens `/platform-admin/user-impersonation`, selects an active tenant user, gives a reason
   (and an optional ticket reference), and starts the session.
2. The service validates the feature flag, the platform-admin policy, the target (active, not the actor, no
   active support-access session, no other active impersonation), and creates:
   - the durable `UserImpersonationSession` row;
   - a target OP session;
   - the server-side session key that binds the browser to the impersonation.
3. Subsequent normal requests receive the target overlay. Control paths keep the administrator.
4. Ending the session — from the banner, the start page, the history page, or logout — marks the record
   `Ended`, revokes the target OP session and the refresh tokens it issued, and clears the session key.
5. If the browser disappears, a background cleanup service marks overdue sessions `Expired` and revokes the
   target session and its tokens. Live requests also detect expiry.
6. History is available at `/platform-admin/user-impersonation/history`, with filters for actor, target, tenant
   and date.

The administrator's cookie cannot be used to switch tenants or sign in as another user while an impersonation
session is active; the control surfaces explain that the session must be ended first. Real impersonation and
read-only support access are mutually exclusive.

## Enabling it

Impersonation is **off by default** and should stay off unless the deployment needs it.

```yaml
Auth:
  EnableUserImpersonation: true
```

Environment-variable form:

```bash
Auth__EnableUserImpersonation=true
```

The development compose stack and the E2E suite enable it explicitly. Enabling it does not grant any new role:
the caller still needs the platform-admin role in the platform realm.

## Operational guidance

- **Keep the flag off in production unless there is an approved support workflow.** The feature is high-risk
  because it grants writable access to a user's identity.
- **Require a reason and, where possible, a ticket reference.** Both are stored on the durable session and
  emitted in the audit event.
- **Retain the history.** `UserImpersonationSessions` is the audit source for actor, target, tenant, reason and
  lifecycle timestamps. Apply the deployment's normal audit-retention policy.
- **Watch the metrics.** `user_impersonation.starts`, `.stops`, `.expirations`, `.revocations`,
  `.validation_failures` and the `user_impersonation.session_duration` histogram are exported from the
  `MrWhoOidc.WebAuth` meter.
- **Do not substitute impersonation for tenant support access.** Use read-only support access for routine
  troubleshooting; reserve impersonation for cases that genuinely require the user's permissions.

## Difference from related features

| | Real user impersonation | Tenant support access | Delegated access |
| --- | --- | --- | --- |
| Actor | Platform admin, kept in `act.sub` | Platform admin, kept as the request actor | Delegator |
| Subject / `sub` | Target tenant user | Empty tenant principal | Delegator |
| Writes | Allowed as the target user | Denied (read-only) | Only delegated capabilities |
| Lifetime | 15 min default, 1–60 min | 15 min default, 1–60 min | Up to 7 days for the grant |
| Scope | One tenant user | One tenant | One user + one client |
| Primary use | Reproduce a user-specific problem | Inspect tenant configuration | Let another user act for you |

See [Delegated access and tenant support access](delegated-and-support-access.md) for the other two models.

> The legacy `ImpersonationAuditLogs` table is not used by this feature. The durable audit source is
> `UserImpersonationSessions`; the old table is retained only for backward compatibility and is scheduled for
> removal in a later cleanup migration.
