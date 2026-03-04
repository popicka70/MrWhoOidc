# 15-Minute Developer Quickstart

Get MrWhoOidc running locally in 15 minutes for development and testing.

## Prerequisites

Ensure you have the following installed:

- **Docker Desktop** (v24+) or **Podman** with Docker Compose
- **.NET 9.0 SDK** (for running tests or building)
- **Git** (for cloning the repository)

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

**Optional for development:**

```bash
# Enable Redis for better performance (optional)
REDIS_ENABLED=true

# Enable email for testing (requires MailHog or SMTP)
MAIL_ENABLED=false
```

---

## Step 3: Start Services (3 minutes)

Start the entire stack with Docker Compose:

```bash
docker compose up -d
```

This starts:
- **MrWhoOidc** web application (port 8443)
- **PostgreSQL** database (port 5432)
- **Redis** cache (port 6379, if enabled)

**Verify services are running:**

```bash
docker compose ps
```

You should see:
```
NAME                    STATUS          PORTS
mrwhooidc-webauth-1     Up (healthy)    0.0.0.0:8443->8443/tcp
mrwhooidc-postgres-1    Up (healthy)    0.0.0.0:5432->5432/tcp
mrwhooidc-redis-1       Up (healthy)    0.0.0.0:6379->6379/tcp
```

---

## Step 4: Bootstrap Initial Tenant (2 minutes)

In production mode, the database starts empty. Call the bootstrap endpoint to create the initial tenant and admin user:

```bash
curl -k -X POST https://localhost:8443/bootstrap \
  -H "Content-Type: application/json" \
  -d '{
    "tenantName": "default",
    "tenantDisplayName": "Default Tenant",
    "adminUsername": "admin",
    "adminEmail": "admin@example.com",
    "adminPassword": "AdminPassword123!"
  }'
```

**Expected response:**
```json
{
  "success": true,
  "tenantId": "01hxyz...",
  "adminUserId": "01hxyz...",
  "message": "Bootstrap completed successfully"
}
```

> **Security Note:** Change the default admin password immediately after first login!

---

## Step 5: Verify Deployment (2 minutes)

### Check OIDC Discovery Endpoint

```bash
curl -k https://localhost:8443/.well-known/openid-configuration | jq
```

**Expected:** JSON document with OIDC configuration including:
- `issuer`: `https://localhost:8443`
- `authorization_endpoint`: `https://localhost:8443/authorize`
- `token_endpoint`: `https://localhost:8443/token`
- `jwks_uri`: `https://localhost:8443/jwks`

### Check Health Endpoints

```bash
# Health check
curl -k https://localhost:8443/health

# Readiness check
curl -k https://localhost:8443/ready

# Prometheus metrics
curl -k https://localhost:8443/metrics
```

**Expected:** All should return HTTP 200 with status information.

### Access Admin UI

Open your browser and navigate to:

```
https://localhost:8443/admin
```

Log in with:
- **Username:** `admin`
- **Password:** `AdminPassword123!`

You should see the admin dashboard with tenant management, client configuration, and user management options.

---

## Step 6: Test Authentication Flow (3 minutes)

### Option A: Using the Example Client

1. Navigate to the example client (if deployed):
   ```
   https://localhost:7000
   ```

2. Click "Login" - you'll be redirected to MrWhoOidc

3. Enter credentials and complete login

4. You'll be redirected back with an authorization code

5. The client exchanges the code for tokens and displays user info

### Option B: Manual OAuth Flow

**1. Get Authorization Code:**

```
https://localhost:8443/authorize?
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
curl -k -X POST https://localhost:8443/token \
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
curl -k https://localhost:8443/userinfo \
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
docker compose ps
```

If not running, check logs:

```bash
docker compose logs webauth
```

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
curl -k https://localhost:8443/admin/tenants \
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
https://localhost:8443/.well-known/openid-configuration
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
| OIDC discovery | `curl -k https://localhost:8443/.well-known/openid-configuration` |
| Admin UI | `https://localhost:8443/admin` |
| Bootstrap | `curl -k -X POST https://localhost:8443/bootstrap ...` |

---

**Document Version:** 1.0  
**Last Updated:** 2025-01-XX  
**Maintained By:** MrWhoOidc Team
