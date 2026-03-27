# mrwho-cli — LLM Usage Skill

Use `mrwho-cli` to manage an MrWhoOidc identity-provider server from the command line.
The tool is installed globally and can be invoked directly as `mrwho-cli`.

---

## Authentication

All commands (except `login` and `discovery`) require an active profile.
A profile stores a server URL, an access token, a refresh token, and metadata such as
`isPlatformAdmin` and `tenantSlug`.

### Login (device-code flow)

```bash
mrwho-cli login --server https://auth.example.com/t/<tenant-slug>
```

The command prints a short user-code and a URL. Open the URL in a browser, approve the
device, and the CLI saves the profile automatically.

**Options:**
- `--server` / `-s` — OIDC server URL. Include the `/t/<slug>` path to target a specific tenant (e.g. `https://host/t/default`).
- `--client-id` / `-c` — Override the CLI client ID (auto-discovered when omitted).

### Logout

```bash
mrwho-cli logout [--profile <name>]
```

Clears tokens for the specified or current profile.

### Profile management

```bash
mrwho-cli profile list [--format Table|Json|Yaml]
mrwho-cli profile show [<name>]
mrwho-cli profile switch <name>
mrwho-cli profile remove <name>
```

---

## Global options

These options are available on every command:

| Flag | Short | Description |
|------|-------|-------------|
| `--profile <name>` | `-p` | Use a named profile instead of the default |
| `--server <url>` | `-s` | Override the server URL for this invocation |
| `--format <Table\|Json\|Yaml>` | `-f` | Output format (default: `Table`) |
| `--verbose` | `-v` | Show full exception traces on error |
| `--dry-run` | | Preview what write operations would do without applying changes |
| `--help` | `-h` | Show help for any command |

---

## Tenants  *(platform-admin only)*

```bash
# List tenants
mrwho-cli tenant list [--search <text>] [--format Table|Json|Yaml]

# Get tenant details
mrwho-cli tenant get <slug-or-guid>

# Create a new tenant (seeds admin user + default realm + system clients)
mrwho-cli tenant create \
  --slug acme \
  --name "Acme Corp" \
  --admin-email admin@acme.com \
  --admin-password "s3cure!" \
  [--output ./acme-credentials.json] \
  [--overwrite]

# Update a tenant
mrwho-cli tenant update <slug-or-guid> \
  [--name "New Name"] \
  [--description "Updated desc"] \
  [--admin-email new@acme.com] \
  [--status Active|Suspended] \
  [--max-users 500] \
  [--max-clients 50]

# Soft-delete a tenant (sets status=Deleted; data preserved)
mrwho-cli tenant delete <slug-or-guid> --confirm
```

Requires a profile where `isPlatformAdmin: true`.
Use `--search` on `list` to filter by slug, name, or description.

---

## Realms

Realms partition users, roles, and identity-providers within a tenant.

```bash
# List realms
mrwho-cli realm list [--format Table|Json|Yaml]

# Get one realm
mrwho-cli realm get <guid>

# Create a realm
mrwho-cli realm create --name customers [--display-name "Customer Accounts"] [--allow-unconfirmed-login]

# Update a realm
mrwho-cli realm update <guid> [--display-name "…"] [--allow-unconfirmed-login <true|false>]

# Delete a realm (must have no clients)
mrwho-cli realm delete <guid>
```

---

## Clients

OIDC/OAuth 2.0 client applications registered in a realm.

```bash
# List clients (platform-admins can filter by --tenant <slug>)
mrwho-cli client list [--tenant <slug>] [--format Table|Json|Yaml]

# Get one client
mrwho-cli client get <internal-guid>

# Create a client
mrwho-cli client create \
  --client-id my-app \
  --client-name "My Application" \
  --realm-id <realm-guid> \
  [--scope "openid profile email"] \
  [--grant-types "authorization_code refresh_token"] \
  [--redirect-uris "https://app.example.com/callback"] \
  [--logout-redirect-uris "https://app.example.com/"] \
  [--require-pkce] \
  [--require-consent] \
  [--create-initial-secret] \
  [--output ./my-app-credentials.json] \
  [--overwrite]

# Update a client (only provided fields are changed)
mrwho-cli client update <internal-guid> \
  [--client-name "New Name"] \
  [--require-pkce <true|false>] \
  [--require-consent <true|false>] \
  [--require-par <true|false>] \
  [--scope "openid profile"] \
  [--grant-types "authorization_code" "refresh_token"] \
  [--redirect-uris "https://new.example.com/callback"] \
  [--logout-redirect-uris "https://new.example.com/"] \
  [--backchannel-logout-uri "https://..."] \
  [--frontchannel-logout-uri "https://..."] \
  [--token-auth-method client_secret_post] \
  [--obo-enabled <true|false>] \
  [--allow-local-login <true|false>] \
  [--allow-external-idp <true|false>]

# Delete a client
mrwho-cli client delete <internal-guid>
```

> **Note:** `client get` / `client delete` use the internal **GUID** (from `client list`), not the `client_id` string.
> When `--create-initial-secret` is set, the plaintext secret is written to a JSON file — it is never echoed to the terminal.

---

## Scopes

Custom OAuth/OIDC scopes within a tenant.

```bash
# List scopes
mrwho-cli scope list [--tenant <slug>] [--format Table|Json|Yaml]

# Create a scope
mrwho-cli scope create --name api.read [--description "Read-only API access"] [--is-exposed]

# Update a scope
mrwho-cli scope update <name> [--description "…"] [--is-exposed <true|false>]

# Delete a scope
mrwho-cli scope delete <name>
```

---

## Users

```bash
# List users (supports pagination and search)
mrwho-cli user list [--search <text>] [--skip <n>] [--take <n>] [--format Table|Json|Yaml]

# Get one user
mrwho-cli user get <guid>

# Create a user (credentials written to file, never printed)
mrwho-cli user create \
  --username alice \
  [--email alice@example.com] \
  [--name "Alice Smith"] \
  [--password "s3cure!"] \
  [--output ./alice-credentials.json] \
  [--overwrite]

# Update a user (only provided fields are changed)
mrwho-cli user update <guid> [--name "New Name"] [--email new@example.com]

# Delete a user
mrwho-cli user delete <guid>
```

### User role assignments

```bash
# List roles assigned to a user (shows both realm and client roles)
mrwho-cli user role list <user-guid> [--format Table|Json|Yaml]

# Assign a role to a user
mrwho-cli user role assign <user-guid> --role-id <role-guid>

# Unassign a role from a user
mrwho-cli user role unassign <user-guid> --role-id <role-guid> --confirm
```

---

## Roles

Roles are scoped to a realm within the current tenant.

```bash
# List roles (optionally filter by realm)
mrwho-cli role list [--realm-id <guid>] [--format Table|Json|Yaml]

# Get one role
mrwho-cli role get <guid>

# Create a role
mrwho-cli role create --name editor --realm-id <realm-guid>

# Update a role
mrwho-cli role update <guid> --name "new-name"

# Delete a role
mrwho-cli role delete <guid> --confirm
```

---

## Export / Import

Manifests are JSON files capturing tenant, realm, client, or provider configuration.
Use them for backup, migration, GitOps, and environment promotion.

### Export

```bash
# Export a whole tenant
mrwho-cli export tenant <slug> [--mode obfuscated|full] [--output ./exports] [--overwrite]

# Export a realm, client, or provider (by GUID)
mrwho-cli export realm    <guid>  [--output path] [--overwrite]
mrwho-cli export client   <guid>  [--output path] [--overwrite]
mrwho-cli export provider <guid>  [--output path] [--overwrite]
```

- `--mode obfuscated` (default) redacts secrets.
- `--mode full` includes plaintext secrets — handle with care.

### Import

```bash
# Dry-run / preview: shows what will change without applying
mrwho-cli import preview <manifest.json> [--dry-run] [--realm-id <guid>] [--client-secret <secret>]

# Apply the manifest
mrwho-cli import apply <manifest.json> \
  [--conflict-resolution skip|overwrite|rename] \
  [--realm-id <guid>] \
  [--client-secret <plaintext-secret>] \
  [--dry-run]
```

Always run `preview` before `apply` when importing into a production tenant.

---

## Discovery

Inspect an OIDC server's discovery document.

```bash
mrwho-cli discovery [--server https://host/t/<slug>] [--format Table|Json|Yaml]
```

---

## Identity Providers

Manage upstream OIDC/SAML identity providers.

```bash
# List providers
mrwho-cli provider list [--format Table|Json|Yaml]

# Get one provider
mrwho-cli provider get <guid>

# Create a provider
mrwho-cli provider create \
  --name "Corporate SSO" \
  --type Oidc \
  [--authority https://accounts.google.com] \
  [--client-id xxx] \
  [--client-secret yyy] \
  [--enabled] \
  [--is-default] \
  [--allow-registration]

# Update a provider
mrwho-cli provider update <guid> [--name "…"] [--enabled <true|false>] [--authority "…"]

# Delete a provider
mrwho-cli provider delete <guid>
```

### Provider claim mappings

```bash
# List claim mappings for a provider
mrwho-cli provider claim-mapping list <provider-guid>

# Create a mapping
mrwho-cli provider claim-mapping create <provider-guid> \
  --external-claim groups --local-claim roles [--transform "…"]

# Update a mapping
mrwho-cli provider claim-mapping update <provider-guid> <mapping-guid> \
  [--local-claim roles] [--transform "…"]

# Delete a mapping
mrwho-cli provider claim-mapping delete <provider-guid> <mapping-guid>
```

### Provider keys

```bash
# List keys for a provider
mrwho-cli provider key list <provider-guid>

# Add a key from a JWK file
mrwho-cli provider key add <provider-guid> --jwk-file ./key.json [--active]

# Update key properties
mrwho-cli provider key update <provider-guid> <key-guid> [--active <true|false>] [--publishable <true|false>]

# Delete a key
mrwho-cli provider key delete <provider-guid> <key-guid>
```

---

## Client Secrets

Manage client secret lifecycle (up to 3 active secrets per client for zero-downtime rotation).

```bash
# List all secrets for a client (masked — only metadata)
mrwho-cli client secret list <client-guid> [--format Table|Json|Yaml]

# Create a new secret (written to file, never echoed)
mrwho-cli client secret create <client-guid> \
  [--expires-in-days 90] \
  [--activate] \
  [--description "production-v2"] \
  [--output ./secret.json] \
  [--overwrite]

# Activate a secret
mrwho-cli client secret activate <client-guid> <secret-guid>

# Set a secret as primary
mrwho-cli client secret set-primary <client-guid> <secret-guid>

# Revoke a secret
mrwho-cli client secret revoke <client-guid> <secret-guid>
```

> **Note:** Secret values are **never** printed to the terminal. The `create` command writes them to a file with owner-only (600) permissions.

---

## Client ↔ Provider Links

Control which identity providers appear on a client's login page.

```bash
# List providers linked to a client
mrwho-cli client provider list <client-guid> [--format Table|Json|Yaml]

# Link a provider to a client
mrwho-cli client provider link <client-guid> \
  --provider-id <provider-guid> \
  [--enabled] [--auto-redirect] [--order 1]

# Update a client-provider link
mrwho-cli client provider update <client-guid> <provider-guid> \
  [--enabled <true|false>] [--order 2]

# Unlink a provider from a client
mrwho-cli client provider unlink <client-guid> <provider-guid>
```

---

## Client Scopes (post-creation)

Add or remove scope assignments from an existing client.

```bash
# List scopes assigned to a client
mrwho-cli client scope list <client-guid> [--format Table|Json|Yaml]

# Add a scope
mrwho-cli client scope add <client-guid> --scope api.read

# Remove a scope
mrwho-cli client scope remove <client-guid> --scope api.read --confirm
```

---

## Client Validation

Read-only diagnostic that checks a client's configuration for common issues.

```bash
mrwho-cli client validate <client-guid> [--format Table|Json|Yaml]
```

Checks include:
- Expired or expiring secrets
- Missing redirect URIs for authorization_code flows
- PKCE disabled on public clients
- No scopes assigned
- Token auth method mismatch (e.g. `client_secret_post` with no secrets)
- Missing post-logout redirect URIs

---

## Client Secret Rotation

Compound operation: create new secret → activate → optionally revoke oldest. Performs
zero-downtime rotation in a single command.

```bash
mrwho-cli client rotate-secret <client-guid> \
  [--expires-in-days 90] \
  [--revoke-oldest] \
  [--description "rotated 2026-01"] \
  [--output ./new-secret.json] \
  [--overwrite] \
  [--confirm]
```

- `--revoke-oldest` only revokes a secret if >2 remain after creation (safe minimum).
- The new secret value is written to file, never echoed.

---

## Diagnostics & Troubleshooting

### Health

Consolidated status across all server subsystems.

```bash
mrwho-cli health [--format Table|Json|Yaml]
```

Reports status for: Backchannel Logout, Client Secrets, Global Auth, Issuer Config, Forwarded Headers.

### Who Am I

Show current profile identity, roles, tenant, and token expiry.

```bash
mrwho-cli whoami [--profile <name>]
```

Useful for debugging "permission denied" or "why can't I do X?" situations.

### Audit Logs

Inspect configuration change history.

```bash
mrwho-cli audit list [--skip 0] [--take 50] [--format Table|Json|Yaml]
mrwho-cli audit get <audit-entry-guid>
```

### Back-Channel Logout

Monitor and manage the BCL notification queue.

```bash
# List outbox entries (filter by status)
mrwho-cli bcl outbox [--status pending|failed|succeeded|dead_letter] [--format Table|Json|Yaml]

# Retry a failed notification
mrwho-cli bcl retry <notification-guid>

# Show alert snapshot (pending/failed/dead-letter counts)
mrwho-cli bcl alerts [--format Table|Json|Yaml]
```

### Rate Limits

Inspect rate-limiting policies and events.

```bash
# Overview of all rate-limit policies
mrwho-cli rate-limits overview

# Recent rate-limit events
mrwho-cli rate-limits events [--format Table|Json|Yaml]

# Per-client rate-limit usage
mrwho-cli rate-limits client <client-guid>
```

### License

Inspect license information and usage.

```bash
mrwho-cli license show [--format Table|Json|Yaml]
mrwho-cli license history [--format Table|Json|Yaml]
mrwho-cli license usage
mrwho-cli license limits
```

---

## Common workflows

### 1. First-time setup

```bash
mrwho-cli login --server https://auth.example.com/t/default
mrwho-cli profile show          # verify isPlatformAdmin, tenantSlug
```

### 2. Provision a new OIDC client end-to-end

```bash
# Get the realm GUID
mrwho-cli realm list

# Create the client and generate a secret
mrwho-cli client create \
  --client-id backend-api \
  --client-name "Backend API" \
  --realm-id <realm-guid> \
  --scope "openid profile" \
  --grant-types "client_credentials" \
  --create-initial-secret \
  --output ./backend-api-secret.json
```

### 3. Migrate a tenant to another environment

```bash
# On source
mrwho-cli export tenant myco --mode full --output ./backup

# On target (preview first, then apply)
mrwho-cli import preview ./backup/myco-*.json
mrwho-cli import apply   ./backup/myco-*.json --conflict-resolution overwrite
```

### 4. Bulk-create users from a script

```bash
for user in alice bob carol; do
  mrwho-cli user create --username "$user" --email "$user@example.com" \
    --output "./creds/$user.json" --overwrite
done
```

### 5. Rotate a client secret with zero downtime

```bash
# Quick rotation: create, activate, and revoke oldest in one command
mrwho-cli client rotate-secret <client-guid> --expires-in-days 90 --revoke-oldest --output ./new-secret.json --confirm

# Or do it step-by-step:
mrwho-cli client secret list <client-guid>
mrwho-cli client secret create <client-guid> --expires-in-days 90 --activate --output ./new-secret.json
# Deploy the new secret to your app, then revoke the old one
mrwho-cli client secret revoke <client-guid> <old-secret-guid> --confirm
```

### 6. Validate a client before going live

```bash
mrwho-cli client validate <client-guid>
# Shows errors/warnings: expired secrets, missing redirect URIs, PKCE issues, etc.
```

### 6. Validate a client before going live

```bash
mrwho-cli client validate <client-guid>
# Shows errors/warnings: expired secrets, missing redirect URIs, PKCE issues, etc.
```

### 7. Connect an external IdP to a client

```bash
# Create the provider
mrwho-cli provider create --name "Google" --type Oidc --authority https://accounts.google.com \
  --client-id xxx --client-secret yyy

# Link it to a client
mrwho-cli client provider link <client-guid> --provider-id <provider-guid> --enabled

# Map external claims to local ones
mrwho-cli provider claim-mapping create <provider-guid> --external-claim hd --local-claim tenant
```

### 8. Troubleshoot a failing back-channel logout

```bash
# Check overall health
mrwho-cli health

# Inspect the BCL queue for failures
mrwho-cli bcl outbox --status failed

# Retry a specific notification
mrwho-cli bcl retry <notification-guid>

# Check alert counts
mrwho-cli bcl alerts
```

### 9. Preview changes with --dry-run

```bash
# See what a client update would send without applying
mrwho-cli client update <guid> --client-name "Test" --require-pkce true --dry-run

# Preview a role deletion
mrwho-cli role delete <guid> --confirm --dry-run
```

---

## Output formats

All list/get/show commands support `--format`:

| Format | Use-case |
|--------|----------|
| `Table` | Human-readable terminal output (default) |
| `Json`  | Machine-readable; pipe to `jq` for further processing |
| `Yaml`  | Human-readable structured data; useful in CI scripts |

Example — extract all client IDs with `jq`:

```bash
mrwho-cli client list --format Json | jq '.[].clientId'
```

---

## Multi-profile / multi-tenant tips

- Use `--profile <name>` to target a non-default profile without switching.
- Use `--tenant <slug>` on `client list` and `scope list` when logged in as a platform-admin to filter by tenant.
- Platform-admin operations (`tenant list`, cross-tenant exports) require a profile where `isPlatformAdmin: true`. Log in at `/t/default` (or the platform realm) to get those claims.

---

## Error handling

- On error, the CLI prints `Error: <message>` with an actionable hint and exits with code 1.
- Common HTTP errors are mapped to suggestions:
  - **401** → "Are you logged in? Try: mrwho-cli login"
  - **403** → "Insufficient permissions. Check your role. Try: mrwho-cli whoami"
  - **404** → "Resource not found. Verify the ID and tenant."
  - **409** → "Conflicting resource exists. Check for duplicate names."
  - **429** → "Rate-limited. Try: mrwho-cli rate-limits overview"
- Add `--verbose` to any command to also print the full exception and stack trace.
- Add `--dry-run` to preview write operations (POST/PUT/DELETE) without applying changes.
- If the access token is expired, the CLI automatically uses the refresh token and re-saves the profile — no manual re-login needed unless the refresh token has also expired.
