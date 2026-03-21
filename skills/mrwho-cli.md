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
| `--help` | `-h` | Show help for any command |

---

## Tenants  *(platform-admin only)*

```bash
mrwho-cli tenant list [--search <text>] [--format Table|Json|Yaml]
```

Lists all tenants. Requires a profile where `isPlatformAdmin: true`.
Use `--search` to filter by slug, name, or description.

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

# Delete a user
mrwho-cli user delete <guid>
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

- On error, the CLI prints `Error: <message>` and exits with code 1.
- Add `--verbose` to any command to also print the full exception and stack trace.
- If the access token is expired, the CLI automatically uses the refresh token and re-saves the profile — no manual re-login needed unless the refresh token has also expired.
