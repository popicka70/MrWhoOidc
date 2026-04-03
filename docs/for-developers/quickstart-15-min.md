# 15-Minute Developer Quickstart

Get MrWhoOidc running locally in 15 minutes for development and testing.

Estimated time for the seeded Docker path is 3-5 minutes on a clean machine. The commands below use `docker compose` (Compose V2). If your environment still only provides `docker-compose`, substitute that command name.

## Prerequisites

Ensure you have the following installed:

- **Docker Desktop** (v24+) or **Podman** with Docker Compose
- **.NET 10 SDK** (for running tests, AppHost, or building locally)
- **Git** (for cloning the repository)
- **4 GB RAM recommended** and **2-3 GB free disk space** for containers, images, and build layers

**Optional:**
- **Visual Studio 2022** or **VS Code** with C# extensions
- **PostgreSQL client** (psql, DBeaver, or pgAdmin)

---

## Step 1: Clone Repository (1 minute)

```bash
git clone https://github.com/popicka70/MrWhoOidc.git
cd MrWhoOidc
```

---

## Step 2: Configure Environment (2 minutes)

Copy the example environment file and set required values:

```bash
cp .env.example .env
```

Edit `.env` and set these **required** variables:

```bash
# Required: Strong password for PostgreSQL
POSTGRES_PASSWORD=YourSecurePassword123!

# Required: Public URL (use localhost for development)
OIDC_PUBLIC_BASE_URL=https://localhost:8443
```

For the seeded development stack, the included development certificate already matches `CERT_PASSWORD=changeit`, and `BOOTSTRAP_TOKEN` is not required.

**Optional for development:**

```bash
# Enable Redis for better performance (optional)
REDIS_ENABLED=true

# Enable email for testing (requires MailHog or SMTP)
MAIL_ENABLED=false
```

---

## Step 3: Start the Development Stack (3 minutes)

Start the development stack from `docker-compose.dev.yml`:

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

This starts:
- **MrWhoOidc WebAuth** (port 8443)
- **PostgreSQL** database (port 5432)
- **Redis** cache (port 6379)
- **MailHog** (SMTP/UI on ports 1025 and 8025)
- **OidcDemo** (port 5001)
- **RazorClient** (port 5003)
- **ReactOidcClient** (port 5173)
- **TestApi** (port 7149)

**Verify services are running:**

```bash
docker compose -f docker-compose.dev.yml ps
```

Wait for `webauth`, `postgres`, and the example apps to become healthy.

## Step 4: Sign In to the Seeded Development Tenant (2 minutes)

The development stack auto-seeds the default tenant on first request. Use these development-only credentials:

- **Username:** `admin@mrwho.local`
- **Password:** `Admin123!`

Open `https://localhost:8443/login` or go directly to `https://localhost:8443/admin/clients`.

> The `/bootstrap` endpoint is for fresh production deployments. It is not required for the development compose stack.

---

## Step 5: Verify the Auth Server (2 minutes)

### Check OIDC Discovery Endpoint

```bash
curl -k https://localhost:8443/t/default/.well-known/openid-configuration | jq
```

**Expected:** JSON document with OIDC configuration including:
- `issuer`: `https://localhost:8443/t/default`
- `authorization_endpoint`: `https://localhost:8443/t/default/authorize`
- `token_endpoint`: `https://localhost:8443/t/default/token`
- `jwks_uri`: `https://localhost:8443/jwks`

For a fuller verification pass, run:

```bash
bash ./scripts/verify-installation.sh
```

### Check Health Endpoints

```bash
# Health check
curl -k https://localhost:8443/health
```

**Expected:** HTTP 200 with status information.

### Check the Tenant Discovery Document Used by the Sample Apps

```bash
curl -k https://localhost:8443/t/default/.well-known/openid-configuration | jq
```

The example applications in `docker-compose.dev.yml` use the tenant-scoped issuer at `https://localhost:8443/t/default`.

### Access the Admin UI

Open your browser and navigate to:

```
https://localhost:8443/admin/clients
```

Log in with:
- **Username:** `admin@mrwho.local`
- **Password:** `Admin123!`

You should see the admin dashboard with tenant management, client configuration, and user management options.

---

## Step 6: Explore the Sample Applications (3 minutes)

The development stack exposes several sample applications that already point at the seeded default tenant:

- `https://localhost:5001` - `MrWhoOidc.OidcDemo`
- `https://localhost:5003` - `MrWhoOidc.RazorClient`
- `http://localhost:5173` - `ReactOidcClient`
- `https://localhost:7149/health` - `MrWhoOidc.TestApi`

The primary demo pair is `RazorClient` plus `TestApi`. Sign in to `https://localhost:5003`, then open the secure page to trigger an on-behalf-of token exchange against the sample API.

See [../example-applications-guide.md](../example-applications-guide.md) for the full example matrix.

---

## Step 7: Test an Authentication Flow (3 minutes)

### Option A: Using the Example Client

1. Navigate to the Razor Pages sample:
   ```
  https://localhost:5003
   ```

2. Click "Login" - you'll be redirected to MrWhoOidc

3. Enter credentials and complete login

4. You'll be redirected back with an authorization code

5. The client exchanges the code for tokens and displays user info

### Option B: Manual OAuth Flow

**1. Get Authorization Code:**

```
https://localhost:8443/t/default/authorize?
  client_id=your-client-id&
  redirect_uri=https://localhost:7000/callback&
  response_type=code&
  scope=openid profile email&
  state=abc123&
  code_challenge=xyz&
  code_challenge_method=S256
```

**2. Exchange Code for Tokens:**

```bash
curl -k -X POST https://localhost:8443/t/default/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=authorization_code" \
  -d "code=AUTH_CODE_FROM_STEP_1" \
  -d "redirect_uri=https://localhost:7000/callback" \
  -d "client_id=your-client-id" \
  -d "code_verifier=ORIGINAL_VERIFIER"
```

**Expected response:**
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIs...",
  "token_type": "Bearer",
  "expires_in": 3600,
  "refresh_token": "...",
  "scope": "openid profile email"
}
```

**3. Call UserInfo Endpoint:**

```bash
curl -k https://localhost:8443/t/default/userinfo \
  -H "Authorization: Bearer YOUR_ACCESS_TOKEN"
```

**Expected response:**
```json
{
  "sub": "01hxyz...",
  "name": "Admin User",
  "email": "admin@example.com",
  "email_verified": true
}
```

---

## Common Issues and Solutions

### Issue: Certificate Errors

**Symptom:** Browser shows certificate warning

**Solution:** This is expected for self-signed certificates. Accept the risk for development, or generate proper certificates:

```bash
# Generate development certificate
dotnet dev-certs https --trust
```

### Issue: Connection Refused

**Symptom:** `curl: (7) Failed to connect to localhost port 8443`

**Solution:** Check if containers are running:

```bash
docker compose -f docker-compose.dev.yml ps
```

If not running, check logs:

```bash
docker compose -f docker-compose.dev.yml logs webauth
```

### Issue: Port 8443, 5001, 5003, 5173, or 7149 Is Already In Use

**Symptom:** Docker Compose fails to start one or more containers with an "address already in use" error.

**Solution:** Stop the conflicting process or override the published port mapping before retrying.

```bash
# Linux
sudo lsof -i :8443
sudo lsof -i :5001
```

If you need a deeper checklist, use [../troubleshooting/local-development.md](../troubleshooting/local-development.md).

### Issue: First Startup Looks Stuck

**Symptom:** Containers are still starting after `docker compose -f docker-compose.dev.yml up -d --build`.

**Solution:** The first run has to build multiple images and may take a minute or two. Re-check status before assuming failure:

```bash
docker compose -f docker-compose.dev.yml ps
docker compose -f docker-compose.dev.yml logs --tail=100 webauth
```

### Issue: Need a Production-Like Empty Database

The development stack intentionally auto-seeds data. For a fresh production-style environment, use `docker-compose.yml` and the bootstrap flow documented in [../production-setup-guide.md](../production-setup-guide.md).

### Optional: Aspire AppHost for Local Debugging

If you want to debug the .NET projects without running the full dev compose stack, start the Aspire host instead:

```bash
dotnet run --project MrWhoOidc.AppHost
```

This launches the core auth server and the primary .NET demo applications in an IDE-friendly workflow.

### Issue: Database Connection Failed

**Symptom:** Application logs show "Connection refused" to PostgreSQL

**Solution:** Ensure PostgreSQL is healthy:

```bash
docker compose logs postgres
```

Wait for "database system is ready to accept connections" message.

### Issue: Bootstrap Fails

**Symptom:** Bootstrap endpoint returns error

**Solution:** Check if tenant already exists:

```bash
curl -k https://localhost:8443/platform-admin/api/tenants \
  -H "Authorization: Bearer YOUR_ADMIN_TOKEN"
```

If tenant exists, use the admin UI to create additional tenants.

### Issue: Slow Startup

**Symptom:** Application takes >2 minutes to start

**Solution:** This is normal for first startup due to:
- Database migrations
- Key generation
- JIT compilation

Subsequent starts will be faster. Enable Redis for better performance:

```bash
REDIS_ENABLED=true docker compose up -d
```

### Issue: Port Conflicts

**Symptom:** "Bind for 0.0.0.0:8443 failed: port is already allocated"

**Solution:** Change the port in `docker-compose.yml`:

```yaml
ports:
  - "8444:8443"  # Use 8444 instead of 8443
```

Or stop the conflicting service:

```bash
lsof -i :8443
kill <PID>
```

---

## Next Steps After Setup

### 1. Create a Test Client

In the Admin UI:
1. Navigate to **Admin → Clients**
2. Click **New Client**
3. Configure:
   - **Client ID:** `test-client`
   - **Client Secret:** Generate a secure secret
   - **Redirect URIs:** `https://localhost:7000/callback`
   - **Allowed Scopes:** `openid`, `profile`, `email`
   - **Allowed Grant Types:** Authorization Code
4. Save the client

### 2. Configure an External Identity Provider

To test IdP chaining:
1. Navigate to **Admin → Identity Providers**
2. Click **New Provider**
3. Select provider type (OIDC, OAuth, SAML)

4. Enter provider configuration
5. Test the connection

### 3. Explore the API

Review the OIDC discovery document:

```
https://localhost:8443/t/default/.well-known/openid-configuration
```

Key endpoints:
- `/authorize` - Authorization endpoint
- `/token` - Token endpoint
- `/userinfo` - User info endpoint
- `/jwks` - JSON Web Key Set
- `/logout` - Logout endpoint

### 4. Run the Test Suite

```bash
dotnet test
```

Expected: All tests pass (may take 2-3 minutes)

### 5. Review Documentation

- **[Developer Guide](developer-guide.md)** - Comprehensive development documentation
- **[Admin Guide](admin-guide.md)** - Administrative operations
- **[Protocol Reference](reference/)** - OIDC/OAuth protocol details

---

## Development Workflow

### Making Code Changes

1. **Stop containers:**
   ```bash
   docker compose down
   ```

2. **Make changes** in your IDE

3. **Rebuild and restart:**
   ```bash
   docker compose build webauth
   docker compose up -d
   ```

### Debugging

**Attach debugger to running container:**

1. In Visual Studio: Debug → Attach to Process
2. Select the `dotnet` process in the container
3. Set breakpoints and debug

**Or run locally:**

```bash
# Run without Docker (requires local PostgreSQL)
dotnet run --project MrWhoOidc.WebAuth
```

### Viewing Logs

```bash
# Follow application logs
docker compose logs -f webauth

# Filter by level
docker compose logs webauth | grep "Error"

# Last 100 lines
docker compose logs --tail=100 webauth
```

---

## Cleanup

### Stop Services

```bash
docker compose down
```

### Remove All Data (Fresh Start)

```bash
docker compose down -v
```

⚠️ **Warning:** This deletes all data including tenants, users, and configuration!

---

## Getting Help

- **Documentation:** [docs/index.md](index.md)
- **Issues:** https://github.com/popicka70/MrWhoOidc/issues
- **Discussions:** https://github.com/popicka70/MrWhoOidc/discussions

---

**Quick Reference Card**

| Task | Command |
|------|---------|
| Start services | `docker compose up -d` |
| Stop services | `docker compose down` |
| View logs | `docker compose logs -f webauth` |
| Health check | `curl -k https://localhost:8443/health` |
| OIDC discovery | `curl -k https://localhost:8443/t/default/.well-known/openid-configuration` |
| Admin UI | `https://localhost:8443/admin/clients` |
| Bootstrap | `curl -k -X POST https://localhost:8443/bootstrap ...` |

---

**Document Version:** 1.0  
**Last Updated:** 2025-01-XX  
**Maintained By:** MrWhoOidc Team
