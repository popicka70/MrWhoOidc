# On-Demand Tenant Seeding

## Overview

The on-demand tenant seeding feature allows **platform administrators** to quickly create fully-configured sample tenants via a REST API endpoint. This is useful for:

- **Demos**: Quickly spin up isolated tenant environments for demonstrations
- **Testing**: Create test tenants with known configurations
- **Development**: Rapidly provision development/staging tenants
- **Onboarding**: Accelerate new customer onboarding

## 🔐 Authorization

**Endpoint**: `POST /platform-admin/api/seed-tenant`  
**Policy**: `platform-admin`  
**Rate Limiter**: `rl-admin`

Only users with the `platform-admin` role in the `platform` realm can access this endpoint.

---

## 📋 Request Format

### Endpoint
```
POST /platform-admin/api/seed-tenant
Content-Type: application/json
```

### Request Body
```json
{
  "tenantSlug": "acme-corp",
  "tenantName": "Acme Corporation",
  "adminEmail": "admin@acme-corp.com",    // Optional, defaults to admin@{tenantSlug}.local
  "adminPassword": "SecurePass123!"       // Optional, defaults to Admin123!
}
```

### Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `tenantSlug` | string | ✅ Yes | - | URL-safe identifier (lowercase, hyphens allowed) |
| `tenantName` | string | ✅ Yes | - | Display name for the tenant |
| `adminEmail` | string | ❌ No | `admin@{slug}.local` | Admin user email address |
| `adminPassword` | string | ❌ No | `Admin123!` | Admin user password (plaintext, will be hashed) |

---

## ✅ Response Format

### Success Response (200 OK)
```json
{
  "success": true,
  "tenantId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tenantSlug": "acme-corp",
  "tenantName": "Acme Corporation",
  "adminEmail": "admin@acme-corp.com",
  "adminPassword": "SecurePass123!",
  "adminClientId": "acme-corp-admin",
  "webClientId": "acme-corp-web",
  "loginUrl": "https://localhost:8443/t/acme-corp/Login",
  "adminUrl": "https://localhost:8443/t/acme-corp/Admin/Users"
}
```

### Error Responses

**400 Bad Request** - Validation failure or tenant already exists:
```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Seeding failed",
  "status": 400,
  "detail": "Tenant with slug 'acme-corp' already exists"
}
```

**401 Unauthorized** - Not authenticated:
```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401
}
```

**403 Forbidden** - Not a platform admin:
```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403
}
```

---

## 🏗️ What Gets Created

For each seeded tenant, the following resources are automatically created:

### 1. **Tenant Entity**
- Slug: As specified
- Name: As specified
- Issuer URI: `https://localhost:8443/t/{slug}`
- Status: Active
- Max Users: 100,000
- Max Clients: 1,000

### 2. **Realms** (2)
- **default**: Default realm for standard users
- **admin**: Admin realm for administrative users

### 3. **Roles** (1)
- **admin** role in admin realm

### 4. **Users** (1)
- Username: First part of email (e.g., `admin` from `admin@acme.com`)
- Email: As specified (or default)
- Password: Hashed using Argon2id
- Email verified: ✅ Yes
- Assigned to: admin role in admin realm

### 5. **Clients** (2)

#### Admin Client (`{slug}-admin`)
- **Purpose**: Internal admin portal access
- **Grant Types**: Authorization Code + PKCE
- **Require Consent**: No
- **Redirect URIs**:
  - `https://localhost:8443/t/{slug}/signin-oidc`
  - `http://localhost:8443/t/{slug}/signin-oidc`
- **Post-Logout URIs**:
  - `https://localhost:8443/t/{slug}/signout-callback-oidc`
  - `https://localhost:8443/t/{slug}/`
  - `http://localhost:8443/t/{slug}/signout-callback-oidc`
  - `http://localhost:8443/t/{slug}/`

#### Web Client (`{slug}-web`)
- **Purpose**: Sample web application
- **Grant Types**: Authorization Code + PKCE
- **Require Consent**: No
- **Redirect URIs**:
  - `https://localhost:5001/signin-oidc`
  - `http://localhost:5001/signin-oidc`
- **Post-Logout URIs**:
  - `https://localhost:5001/signout-callback-oidc`
  - `https://localhost:5001/`
  - `http://localhost:5001/signout-callback-oidc`
  - `http://localhost:5001/`

### 6. **Scopes** (5)
- `openid` - OpenID Connect protocol
- `profile` - User profile information
- `email` - Email address
- `roles` - User roles
- `offline_access` - Refresh token support

All scopes are associated with both clients.

---

## 📝 Usage Examples

### Example 1: Create Acme Corp Tenant (Default Credentials)
```bash
curl -X POST https://localhost:8443/platform-admin/api/seed-tenant \
  -H "Content-Type: application/json" \
  -H "Cookie: .AspNetCore.Cookies={auth-cookie}" \
  -d '{
    "tenantSlug": "acme-corp",
    "tenantName": "Acme Corporation"
  }'
```

**Result**:
- Admin Email: `admin@acme-corp.local`
- Admin Password: `Admin123!`
- Login URL: `https://localhost:8443/t/acme-corp/Login`

### Example 2: Create Globex Inc Tenant (Custom Credentials)
```bash
curl -X POST https://localhost:8443/platform-admin/api/seed-tenant \
  -H "Content-Type: application/json" \
  -H "Cookie: .AspNetCore.Cookies={auth-cookie}" \
  -d '{
    "tenantSlug": "globex-inc",
    "tenantName": "Globex Inc.",
    "adminEmail": "admin@globex.com",
    "adminPassword": "MySecurePass456!"
  }'
```

**Result**:
- Admin Email: `admin@globex.com`
- Admin Password: `MySecurePass456!`
- Login URL: `https://localhost:8443/t/globex-inc/Login`

### Example 3: PowerShell Script
```powershell
$body = @{
    tenantSlug = "test-tenant"
    tenantName = "Test Tenant"
    adminEmail = "admin@test.local"
    adminPassword = "Test123!"
} | ConvertTo-Json

$response = Invoke-WebRequest `
    -Uri "https://localhost:8443/platform-admin/api/seed-tenant" `
    -Method POST `
    -ContentType "application/json" `
    -Body $body `
    -UseDefaultCredentials

$result = $response.Content | ConvertFrom-Json
Write-Host "Tenant created: $($result.tenantSlug)"
Write-Host "Login URL: $($result.loginUrl)"
Write-Host "Admin email: $($result.adminEmail)"
Write-Host "Admin password: $($result.adminPassword)"
```

### Example 4: JavaScript (Fetch API)
```javascript
const response = await fetch('/platform-admin/api/seed-tenant', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
  },
  body: JSON.stringify({
    tenantSlug: 'demo-tenant',
    tenantName: 'Demo Tenant',
    adminEmail: 'admin@demo.com',
    adminPassword: 'Demo123!'
  })
});

const result = await response.json();
console.log('Tenant created:', result.tenantSlug);
console.log('Login at:', result.loginUrl);
console.log('Admin portal:', result.adminUrl);
```

---

## 🔒 Security Considerations

### Password Security
- ⚠️ **Development Only**: Default password `Admin123!` is NOT secure for production
- ✅ **Production**: Always specify a strong, unique password via `adminPassword`
- ✅ **Best Practice**: Force password change on first login (manual implementation required)

### Authorization
- ✅ Only platform admins can seed tenants (enforced by `platform-admin` policy)
- ✅ Tenant isolation ensures seeded tenants cannot interfere with each other
- ✅ All passwords are hashed using Argon2id before storage

### Rate Limiting
- ✅ Subject to `rl-admin` rate limiter
- Prevents abuse/DoS attacks

### Audit Logging
- ✅ All seeding operations are logged with tenant slug and admin email
- Check logs: `docker compose logs webauth | grep "Created tenant"`

---

## 🧪 Testing the Feature

### Step 1: Login as Platform Admin
```bash
# Navigate to login page
https://localhost:8443/Login

# Credentials (from auto-seed)
Email: admin@mrwho.local
Password: Admin123!
```

### Step 2: Seed a Test Tenant
```bash
curl -X POST https://localhost:8443/platform-admin/api/seed-tenant \
  -H "Content-Type: application/json" \
  -H "Cookie: .AspNetCore.Cookies={your-auth-cookie}" \
  -d '{
    "tenantSlug": "test",
    "tenantName": "Test Tenant"
  }'
```

### Step 3: Login to New Tenant
```bash
# Navigate to tenant login page
https://localhost:8443/t/test/Login

# Credentials
Email: admin@test.local
Password: Admin123!
```

### Step 4: Access Tenant Admin Portal
```bash
# After login, navigate to:
https://localhost:8443/t/test/Admin/Users
```

---

## 🔍 Troubleshooting

### Issue: "Tenant with slug 'xxx' already exists"
**Cause**: Trying to create a tenant that already exists.  
**Solution**: Choose a different slug or delete the existing tenant via Platform Admin UI.

### Issue: "Validation failed: TenantSlug is required"
**Cause**: Missing or empty `tenantSlug` in request body.  
**Solution**: Ensure `tenantSlug` is provided and not empty.

### Issue: 403 Forbidden
**Cause**: User does not have `platform-admin` role.  
**Solution**: Ensure you're logged in as a platform admin (e.g., `admin@mrwho.local`).

### Issue: Cannot login to new tenant
**Cause**: Incorrect credentials or tenant not fully seeded.  
**Solution**:
1. Check seeding logs: `docker compose logs webauth | grep "Created tenant"`
2. Verify email/password matches what was specified (or defaults)
3. Check database: `SELECT * FROM tenants WHERE slug = 'your-slug';`

---

## 📊 Database Impact

### Tables Modified
- `tenants` (1 row)
- `realms` (2 rows: default, admin)
- `roles` (1 row: admin)
- `users` (1 row: admin user)
- `clients` (2 rows: admin client, web client)
- `user_role_assignments` (1 row: admin → admin role)
- `scopes` (5 rows if not already present: openid, profile, email, roles, offline_access)
- `client_scopes` (10 rows: 5 scopes × 2 clients)

### Transaction Safety
✅ All operations wrapped in a single database transaction (via EF Core SaveChanges)  
✅ Rollback on any failure  
✅ Thread-safe (each request gets its own DbContext scope)

---

## 🚀 Integration with Platform Admin UI

### Future Enhancement: UI Button
Could add a "Seed Sample Tenant" button to Platform Admin UI:

```razor
<!-- Pages/PlatformAdmin/Tenants/Index.cshtml -->
<button onclick="seedSampleTenant()" class="btn btn-success">
  <i class="bi bi-plus-circle"></i> Seed Sample Tenant
</button>

<script>
async function seedSampleTenant() {
  const slug = prompt("Enter tenant slug (e.g., 'demo-tenant'):");
  const name = prompt("Enter tenant name (e.g., 'Demo Tenant'):");
  
  if (!slug || !name) return;
  
  const response = await fetch('/platform-admin/api/seed-tenant', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tenantSlug: slug, tenantName: name })
  });
  
  if (response.ok) {
    const result = await response.json();
    alert(`Tenant created!\n\nLogin at: ${result.loginUrl}\nEmail: ${result.adminEmail}\nPassword: ${result.adminPassword}`);
    location.reload();
  } else {
    const error = await response.json();
    alert(`Error: ${error.detail}`);
  }
}
</script>
```

---

## 📖 Related Documentation

- **Platform Admin Setup**: `/docs/platform-admin-setup-summary.md`
- **Tenant Creation UI Flow**: `/docs/tenant-creation-ui-flow.md`
- **Multi-Tenancy Backlog**: `/docs/multitenancy-backlog.md`
- **Auto-Seeding**: AutoSeedMiddleware creates default tenant on first request

---

## 🎯 Use Cases

### Use Case 1: Demo Environment
**Scenario**: Sales team needs isolated demo environments for prospects.

**Solution**:
```bash
# Create Demo A for Prospect A
POST /platform-admin/api/seed-tenant
{ "tenantSlug": "demo-prospect-a", "tenantName": "Prospect A Demo" }

# Create Demo B for Prospect B
POST /platform-admin/api/seed-tenant
{ "tenantSlug": "demo-prospect-b", "tenantName": "Prospect B Demo" }
```

### Use Case 2: Multi-Environment Testing
**Scenario**: QA needs dev/staging/prod tenants for testing.

**Solution**:
```bash
# Dev environment
POST /platform-admin/api/seed-tenant
{ "tenantSlug": "dev", "tenantName": "Development" }

# Staging environment
POST /platform-admin/api/seed-tenant
{ "tenantSlug": "staging", "tenantName": "Staging" }

# Prod simulation
POST /platform-admin/api/seed-tenant
{ "tenantSlug": "prod-sim", "tenantName": "Production Simulation" }
```

### Use Case 3: Customer Onboarding
**Scenario**: New customer signs up, needs isolated tenant immediately.

**Solution**:
```javascript
// In onboarding workflow
await fetch('/platform-admin/api/seed-tenant', {
  method: 'POST',
  body: JSON.stringify({
    tenantSlug: customerSlug,
    tenantName: customerName,
    adminEmail: customerEmail,
    adminPassword: generateSecurePassword()
  })
});

// Send welcome email with credentials
sendWelcomeEmail(customerEmail, {
  loginUrl: `https://yourapp.com/t/${customerSlug}/Login`,
  password: generatedPassword
});
```

---

## ✅ Summary

**On-demand tenant seeding** provides a fast, automated way to create fully-configured sample tenants for demos, testing, and development. Platform admins can invoke a single REST endpoint to provision:

- ✅ Tenant entity with active status
- ✅ Default and admin realms
- ✅ Admin role and user
- ✅ Two pre-configured clients (admin portal + web app)
- ✅ Standard OIDC scopes (openid, profile, email, roles, offline_access)
- ✅ Complete isolation from other tenants

**Security**: Platform-admin authorization required, passwords hashed with Argon2id, rate-limited, fully audited.

**Next Steps**: Use this feature to rapidly provision test environments, demo instances, or bootstrap new customer tenants!
