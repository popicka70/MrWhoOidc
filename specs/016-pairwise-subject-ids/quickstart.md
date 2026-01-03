# Quickstart: Pairwise Subject Identifiers

## Goal

Verify that pairwise subject identifiers work per client/sector and are advertised in discovery.

## Preconditions

- Feature implemented per spec.
- You have an admin account to edit clients.
- You can authenticate as a test user at least twice.

## Steps

1. Configure a client as **public**.
   - Authenticate and record `sub` from ID token (or UserInfo).

2. Configure another client as **pairwise**.
   - Leave `SectorIdentifierUri` empty (fallback mode).
   - Ensure the client has at least one redirect URI.
   - Authenticate twice; verify `sub` is stable across logins.
   - Verify the pairwise `sub` is different from the public client’s `sub`.

3. Configure two pairwise clients to share a sector.
   - Set the same `SectorIdentifierUri` (HTTPS) for both.
   - Ensure the URI’s JSON array includes both clients’ redirect URIs.
   - Authenticate to both; verify `sub` matches across both clients.

4. Configure a third pairwise client with a different sector.
   - Authenticate; verify `sub` differs.

5. Validate discovery metadata.
   - Fetch `/.well-known/openid-configuration`.
   - Verify `subject_types_supported` includes both `public` and `pairwise`.

## Expected Results

- Public client always returns public `sub`.
- Pairwise client returns persisted base64url random `sub` per (tenant, user, sector).
- Sector grouping controls whether two pairwise clients share `sub`.
- Discovery advertises both subject types.
