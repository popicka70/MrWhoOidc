# User Registration and Tenant Enrollment

Updated: 2026-05-24

This guide describes how new users enter MrWhoOidc tenants through self-service registration, tenant invitations, and tenant domain claims.

## Concepts

MrWhoOidc separates global identity from tenant membership:

- `UserAccount` is the global identity. It owns the password, lockout state, MFA state, email verification state, and tenant memberships.
- `User` is the tenant-scoped user profile used inside a specific tenant.
- `UserTenantMembership` links a global account to one tenant.

The platform registration page is `/Registrations`. Tenant-specific registration is available at `/t/{tenantSlug}/Registrations` when the tenant enables that path. Tenant-specific sign-in uses `/t/{tenantSlug}/login`. The root sign-in page uses `/DiscoverTenant` when platform settings allow tenant discovery.

Tenant admins choose how self-service registration can assign users to their tenant from **Admin -> Settings -> User Registration**:

| Mode | Behavior |
| --- | --- |
| `platform-only` | Users join through the platform registration path, invitations, domain claims, or client policy. Tenant-specific `/t/{tenantSlug}/Registrations` is disabled. This is the default. |
| `tenant-only` | Users must use `/t/{tenantSlug}/Registrations` for self-service registration into the tenant. Platform registration redirects invitation users to the tenant path and rejects platform auto-assignment for matching domain claims. |
| `both` | Both `/Registrations` and `/t/{tenantSlug}/Registrations` can assign users to the tenant. |

Tenant-specific registration always carries the tenant context in the URL, so new registrations are created for that tenant without relying on domain discovery or return URL inference. Tenant-specific registration does not auto-approve users by itself; without an invitation, auto-join domain claim, or client auto-approval policy, the registration remains pending for tenant admin review.

## Registration Outcomes

Manual or external registration can produce one of these outcomes:

| Context | Tenant target | Approval behavior |
| --- | --- | --- |
| Create new tenant | New tenant from the form | Auto-approved as tenant admin |
| Invitation link | Invitation tenant | Auto-approved for that tenant after the invite is accepted |
| Verified auto-join domain claim | Matching claimed domain's tenant | Auto-approved for that tenant |
| Client auto-approval policy | Client tenant | Auto-approved when the client policy allows it |
| Tenant-specific registration path | Tenant from `/t/{tenantSlug}/Registrations` | Pending registration for tenant admin review unless another auto-approval source applies |
| No auto-approval source | Current or resolved tenant | Pending registration for admin review |

Invitations take precedence over domain claims. A user registering from an invitation must use the invited email address.

## Tenant-Specific Registration Customization

Tenant admins can customize the tenant registration page from **Admin -> Settings -> User Registration**:

- Registration mode: platform-only, tenant-only, or both.
- Tenant registration heading.
- Tenant registration intro text.
- Tenant registration image URL.

The page also uses tenant branding from **Admin -> Branding**: logo, primary color, and accent color. This keeps shared visual identity in one place while allowing registration-specific copy and imagery in tenant settings.

The same settings are available through the tenant admin API and CLI:

```bash
mrwho-cli registration get
mrwho-cli registration set --mode both \
  --headline "Join Contoso" \
  --intro "Create your Contoso account." \
  --hero-image-url https://cdn.example.com/contoso-registration.jpg
mrwho-cli registration set --mode tenant-only
mrwho-cli registration set --mode platform-only
```

The API surface is tenant-scoped: `GET /admin/api/registration-settings` and `PUT /admin/api/registration-settings`, also available under `/t/{tenantSlug}/admin/api/registration-settings`.

## Tenant Invitations

Tenant admins invite users from **Admin -> Invitations**.

1. Open `/admin/invitations` in the tenant context.
2. Enter the invited email, optional display name, role, and validity period.
3. Send the generated invitation link to the user.
4. The invited user can either sign in with an existing matching account or register with the invitation link.

Invitation behavior:

- The invitation fixes the tenant and email address.
- New invited users are auto-approved into the invitation tenant.
- Existing users must sign in with the invited email before the invitation can be accepted.
- Tenant-admin invitations also grant the tenant-admin role.
- The invitation status moves from `Pending` to `Accepted`, `Revoked`, or `Expired`.

For sign-in, prefer the tenant URL from the invitation flow, such as `/t/default/login`, rather than depending on root discovery state.

### CLI and MCP automation

Tenant admins can manage invitations without the browser:

```bash
mrwho-cli invitation create --email user@example.com --display-name "Example User"
mrwho-cli invitation list
mrwho-cli invitation revoke <invitation-id> --confirm
```

The CLI also exposes MCP tools named `invitation_list`, `invitation_create`, and `invitation_revoke`, so LLM agents can create or clean up invitations after a human has authenticated the CLI profile.

## Tenant Domain Claims

Tenant admins manage claimed domains from **Admin -> Domain claims** at `/admin/domain-claims`.

A domain claim says that an email domain belongs to exactly one active tenant. The platform enforces this with both service validation and a database unique index for non-revoked claims.

Supported enrollment modes:

| Mode | Behavior |
| --- | --- |
| `AutoJoin` | Matching emails can discover the tenant and are auto-approved into it during registration. |
| `RequireInvitation` | The domain is reserved by the tenant, but users still need an invitation or another approval path. |
| `Disabled` | Reserved for stored/inactive configuration; the admin UI does not create new disabled claims. |

Domain claim rules:

- Enter only the domain, such as `example.com`; do not enter an email address or URL.
- Domains are normalized to lowercase ASCII and trailing dots are removed.
- Common public mailbox domains, such as `gmail.com` and `outlook.com`, cannot be claimed.
- A non-revoked domain can belong to only one tenant at a time.
- Revoking a claim removes it from discovery and auto-join resolution.

Current verification behavior: the admin UI creates claims as `Verified` immediately. Treat tenant-admin access as trusted for this workflow. The model includes DNS verification metadata fields for a future ownership verification flow, but automated DNS TXT verification is not currently enforced.

## Domain Auto-Enrollment Flow

When an unauthenticated user enters `user@example.com` at `/DiscoverTenant`, tenant discovery checks:

1. Existing tenant user profiles with the same normalized email.
2. Verified alternative emails.
3. Verified `AutoJoin` tenant domain claims.

If exactly one tenant matches, discovery redirects to `/t/{tenantSlug}/login` with the email prefilled.

When a new user registers manually with an email that matches a verified `AutoJoin` claim, registration targets that tenant and is auto-approved. The created global `UserAccount` receives the registration password, and a tenant-scoped `User` plus active membership are created for the claimed tenant.

## External IdP Registration

External IdPs can be enabled for registration from **Admin -> Providers** with the provider's **Allow Registration** setting. Only enabled providers with registration allowed appear on registration pages. `/Registrations` shows registration-enabled providers from the default tenant. `/t/{tenantSlug}/Registrations` shows registration-enabled providers from that tenant.

External IdP domain enrollment is intentionally conservative:

- Platform login never auto-enrolls a user into a tenant.
- Tenant external login can use domain auto-enrollment only when the mapped claims include `email_verified=true`.
- If a verified email domain claim points at a different tenant than the current tenant/client context, the domain enrollment is skipped.
- Existing account linking and client auto-approval policies still apply independently.

## Admin Review

Pending registrations remain visible in **Admin -> Registrations** for tenant administrators. Domain auto-join and invitations reduce the need for manual review, but they do not bypass email confirmation messaging or global account controls.

## Unassigned Platform Accounts

Some global accounts may have no active tenant membership. These accounts can sign in only far enough to manage their own account state; they do not receive tenant admin or tenant application access.

Platform admins can review and terminate these accounts from **Platform Admin -> Unassigned Users** at `/platform-admin/users/unassigned`.

The same operations are available through the CLI with a platform-admin profile:

```bash
mrwho-cli user unassigned list
mrwho-cli user unassigned get <user-account-id>
mrwho-cli user unassigned terminate <user-account-id> --confirm
```

Termination is allowed only while the account still has no active tenant memberships. If the account is assigned to a tenant before the operation completes, the platform API rejects the termination.

## Operational Checks

- Check **Admin -> Domain claims** before adding a domain to confirm it is not already claimed.
- Use **Admin -> Invitations** for contractor, shared mailbox, or non-domain users.
- Use **Platform Admin -> Unassigned Users** to clean up global accounts that never joined a tenant or whose tenant access has ended.
- Use `RequireInvitation` for domains that should be reserved but not self-service auto-joined.
- Keep tenant-admin membership tightly controlled because tenant admins can create verified domain claims in the current implementation.

## Test Coverage

Focused coverage lives in:

- `MrWhoOidc.UnitTests/Services/TenantDomainClaimServiceTests.cs`
- `MrWhoOidc.UnitTests/Services/TenantEnrollmentServiceTests.cs`
- `MrWhoOidc.UnitTests/PlatformUnassignedUsersApiTests.cs`
- `MrWhoOidc.UnitTests/MultiTenancy/SettingsOverrideTests.cs`
- `MrWhoOidc/e2e/tests/test_tenant_domain_claims.py`
- `MrWhoOidc/e2e/tests/test_tenant_enrollment.py`
- `MrWhoOidc/e2e/tests/test_tenant_registration_settings.py`
- `MrWhoOidc/e2e/tests/test_cli_operations.py` (`TestCliUnassignedUsers`)

Use [../e2e/README.md](../e2e/README.md) for canonical browser E2E setup and run commands.
