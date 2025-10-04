# On-Demand Tenant Seeding - Quick Reference

## 🚀 Quick Start

### Endpoint
```
POST /platform-admin/api/seed-tenant
Authorization: platform-admin policy required
Content-Type: application/json
```

### Minimal Request
```json
{
  "tenantSlug": "acme-corp",
  "tenantName": "Acme Corporation"
}
```

### Full Request
```json
{
  "tenantSlug": "acme-corp",
  "tenantName": "Acme Corporation",
  "adminEmail": "admin@acme-corp.com",
  "adminPassword": "SecurePass123!"
}
```

---

## ✅ What Gets Created

| Resource | Count | Details |
|----------|-------|---------|
| **Tenant** | 1 | Slug, name, issuer URI, active status |
| **Realms** | 2 | default, admin |
| **Roles** | 1 | admin (in admin realm) |
| **Users** | 1 | Admin user with verified email |
| **Clients** | 2 | {slug}-admin, {slug}-web |
| **Scopes** | 5 | openid, profile, email, roles, offline_access |
| **Scope Associations** | 10 | 5 scopes × 2 clients |
| **Role Assignments** | 1 | Admin user → admin role |

**Total**: 23 database rows created per tenant

---

## 🔐 Default Credentials

If not specified:
- **Email**: `admin@{tenantSlug}.local`
- **Password**: `Admin123!` ⚠️ (Development only!)

---

## 📋 Success Response

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

---

## 🧪 cURL Example

```bash
curl -X POST https://localhost:8443/platform-admin/api/seed-tenant \
  -H "Content-Type: application/json" \
  -H "Cookie: .AspNetCore.Cookies={auth-cookie}" \
  -d '{
    "tenantSlug": "demo",
    "tenantName": "Demo Tenant",
    "adminEmail": "admin@demo.com",
    "adminPassword": "Demo123!"
  }'
```

---

## ⚡ PowerShell One-Liner

```powershell
Invoke-RestMethod -Uri "https://localhost:8443/platform-admin/api/seed-tenant" -Method POST -ContentType "application/json" -Body '{"tenantSlug":"test","tenantName":"Test Tenant"}' -UseDefaultCredentials | ConvertTo-Json
```

---

## 🎯 Common Use Cases

### 1️⃣ Create Demo Tenant
```bash
POST /platform-admin/api/seed-tenant
{ "tenantSlug": "demo-prospect", "tenantName": "Prospect Demo" }
```

### 2️⃣ Create Dev Environment
```bash
POST /platform-admin/api/seed-tenant
{ "tenantSlug": "dev", "tenantName": "Development" }
```

### 3️⃣ Create Customer Tenant
```bash
POST /platform-admin/api/seed-tenant
{
  "tenantSlug": "acme-corp",
  "tenantName": "Acme Corporation",
  "adminEmail": "admin@acme-corp.com",
  "adminPassword": "GeneratedSecurePassword123!"
}
```

---

## ❌ Common Errors

| Error | Cause | Solution |
|-------|-------|----------|
| 400: "Tenant slug is required" | Missing `tenantSlug` | Add `tenantSlug` to request |
| 400: "Tenant already exists" | Duplicate slug | Choose different slug |
| 401: Unauthorized | Not authenticated | Login first |
| 403: Forbidden | Not platform admin | Use platform admin account |
| 429: Too Many Requests | Rate limited | Wait before retrying |

---

## 🔍 Verification Steps

### 1. Check Logs
```bash
docker compose logs webauth | grep "Created tenant"
```

### 2. Check Database
```sql
SELECT slug, name, status FROM tenants WHERE slug = 'your-slug';
```

### 3. Test Login
```
Navigate to: https://localhost:8443/t/{your-slug}/Login
Email: admin@{your-slug}.local (or specified)
Password: Admin123! (or specified)
```

### 4. Access Admin UI
```
After login: https://localhost:8443/t/{your-slug}/Admin/Users
```

---

## 🔒 Security Notes

✅ **Authorization**: Platform-admin policy enforced  
✅ **Password Hashing**: Argon2id  
✅ **Rate Limiting**: rl-admin applied  
✅ **Audit Logging**: All operations logged  
⚠️ **Default Password**: `Admin123!` is NOT secure for production!

---

## 📂 File Locations

| File | Location |
|------|----------|
| Service Implementation | `MrWhoOidc.WebAuth/Services/TenantSeedingService.cs` |
| API Endpoint | `MrWhoOidc.WebAuth/Infrastructure/EndpointMapping/AdminApiEndpointMappingExtensions.cs` |
| Service Registration | `MrWhoOidc.WebAuth/Program.cs` (line 125) |
| Full Documentation | `docs/on-demand-tenant-seeding.md` |

---

## 📊 Performance

- **Average Seeding Time**: ~200-500ms
- **Database Rows Created**: 23
- **SaveChanges Calls**: ~6-8 (batched per entity type)
- **Transaction**: Single EF Core transaction (atomic)

---

## 🚦 Rate Limits

**Policy**: `rl-admin`  
**Limits**: Same as other admin endpoints  
**Recommendation**: 1 seed operation per 5 seconds max

---

## 🎬 Demo Script

```bash
# 1. Login as platform admin
echo "Logging in as platform admin..."
curl -X POST https://localhost:8443/Login \
  -d "email=admin@mrwho.local&password=Admin123!"

# 2. Seed demo tenant
echo "Creating demo tenant..."
curl -X POST https://localhost:8443/platform-admin/api/seed-tenant \
  -H "Content-Type: application/json" \
  -d '{
    "tenantSlug": "demo",
    "tenantName": "Demo Tenant"
  }' | jq .

# 3. Output will show:
# - loginUrl: https://localhost:8443/t/demo/Login
# - adminEmail: admin@demo.local
# - adminPassword: Admin123!

# 4. Login to demo tenant
echo "Login at: https://localhost:8443/t/demo/Login"
```

---

## 🔗 Related Endpoints

| Endpoint | Purpose |
|----------|---------|
| `GET /PlatformAdmin/Tenants` | List all tenants (UI) |
| `POST /PlatformAdmin/Tenants/Create` | Create tenant (UI form) |
| `POST /platform-admin/api/seed-tenant` | Create tenant (API, with seeding) |
| `GET /health` | Health check |
| `GET /health/backchannel` | Backchannel logout health |

---

## 💡 Pro Tips

1. **Use unique slugs**: Slug must be unique across all tenants
2. **Strong passwords**: Always specify strong `adminPassword` in production
3. **Email verification**: Seeded users have `EmailVerified = true` by default
4. **Scope reuse**: Standard scopes (openid, profile, etc.) are reused if they already exist
5. **Logging**: Check logs for detailed seeding progress and troubleshooting

---

## 📖 See Also

- **Full Documentation**: `docs/on-demand-tenant-seeding.md`
- **Platform Admin Setup**: `docs/platform-admin-setup-summary.md`
- **Tenant Creation UI**: `docs/tenant-creation-ui-flow.md`
- **Multi-Tenancy Backlog**: `docs/multitenancy-backlog.md`
