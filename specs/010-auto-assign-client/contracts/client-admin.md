# Contracts: Client Admin (Auto-Assign New Users)

## Purpose

Document the contract changes needed so admins can configure the per-client auto-assignment behavior.

## Client fields (add/edit)

A client has a boolean field:

- `autoAssignNewUsersToClient`
  - Meaning: If enabled, brand-new users created during this client’s sign-in journey are automatically assigned to this client.
  - Default: `false`

## Admin UI contract

- **Add Client**: must allow setting `autoAssignNewUsersToClient`.
- **Edit Client**: must display current `autoAssignNewUsersToClient` and allow changing it.

## Backing behavior contract

When `autoAssignNewUsersToClient` is `true`:

- New user created via local registration (initiated from this client’s sign-in journey) MUST receive a User-Client assignment to this client.
- New user created via first-time external IdP sign-in (initiated from this client’s sign-in journey) MUST receive a User-Client assignment to this client.

When `autoAssignNewUsersToClient` is `false`:

- No auto-assignment occurs.

## Notes

- This feature may be exposed via existing admin pages and/or admin APIs, depending on current project patterns. If an API DTO exists for client create/update, it must include `autoAssignNewUsersToClient`.
