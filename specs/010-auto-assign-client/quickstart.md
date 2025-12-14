# Quickstart: Auto-Assign New Users To Client

## Goal

Enable a client to automatically assign brand-new users to that client when the user creates an account during the client’s sign-in journey (local registration or first-time external IdP sign-in).

## Configure

1. In the admin UI, add or edit a client.
2. Enable the setting “Auto-assign new users to this client”.
3. Save the client.

## Verify: Local registration

1. Start an authorization/sign-in journey for the client.
2. On the login page, choose “Register new user”.
3. Create a new user.
4. Complete sign-in.
5. Confirm the new user is assigned to the client:
	- Admin UI → Users → select the user → Clients tab ("Client assignments").

## Verify: External IdP (first-time sign-in)

1. Start an authorization/sign-in journey for the client.
2. Choose an external provider.
3. Complete external sign-in.
4. Confirm the new user is assigned to the client:
	- Admin UI → Users → select the user → Clients tab ("Client assignments").

## Expected behavior

- If the client setting is disabled, no auto-assignment occurs.
- Existing users are never auto-assigned (no changes to existing assignments).
- Auto-assignment is only performed when the flow is tied to a valid client sign-in attempt.
