# Tenant Creation UI Flow - Quick Reference

## Visual Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                    PLATFORM ADMIN WORKFLOW                          │
└─────────────────────────────────────────────────────────────────────┘

Step 1: Login as Platform Admin
├─ URL: https://localhost:8443/Login
├─ Credentials: Platform admin user
└─ Requires: "platform-admin" policy claim

                              ↓

Step 2: Navigate to Platform Admin UI
├─ URL: https://localhost:8443/PlatformAdmin/Tenants
├─ View: List of all tenants (or empty state)
└─ Action: Click [+ Create Tenant] button

                              ↓

Step 3: Fill Out Tenant Creation Form
├─ URL: https://localhost:8443/PlatformAdmin/Tenants/Create
│
├─ REQUIRED FIELDS:
│  ├─ Slug:          acme-corp              (URL-safe identifier)
│  ├─ Name:          Acme Corporation       (Display name)
│  ├─ Admin Email:   admin@acme-corp.com    (First admin user)
│  └─ Admin Pass:    SecurePass123!         (Min 8 chars)
│
├─ OPTIONAL FIELDS:
│  ├─ Description:   Industrial supply company
│  ├─ Status:        Active / Pending Setup
│  ├─ Billing Plan:  Free / Starter / Pro / Enterprise
│  ├─ Max Users:     10000 (0 = unlimited)
│  ├─ Max Clients:   100 (0 = unlimited)
│  ├─ Logo URL:      https://acme.com/logo.png
│  ├─ Primary Color: #0d6efd (hex)
│  └─ Accent Color:  #6610f2 (hex)
│
└─ Action: Click [✅ Create Tenant] button

                              ↓

Step 4: Server Processing (Behind the Scenes)
├─ Validation:
│  ├─ ✅ Slug unique check (must not exist)
│  ├─ ✅ Email unique check (across all tenants)
│  ├─ ✅ Slug format (lowercase, hyphens only)
│  ├─ ✅ Password strength (min 8 chars)
│  └─ ✅ Color format (hex colors)
│
├─ Database Operations (transaction):
│  ├─ 1. Create Tenant record
│  ├─ 2. Generate Issuer URI:
│  │      MultiTenant: https://localhost:8443/t/acme-corp
│  │      SingleTenant: https://localhost:8443
│  ├─ 3. Create default Realm for tenant
│  ├─ 4. Hash admin password (Argon2id)
│  └─ 5. Create first admin User (EmailVerified=true)
│
└─ Redirect: Back to /PlatformAdmin/Tenants with success message

                              ↓

Step 5: Tenant Created Successfully!
├─ View: Tenant list shows new tenant
├─ Details:
│  ├─ Slug:      acme-corp
│  ├─ Name:      Acme Corporation
│  ├─ Issuer:    https://localhost:8443/t/acme-corp
│  ├─ Status:    ✅ Active
│  ├─ Users:     1 / 10000
│  ├─ Clients:   0 / 100
│  └─ Created:   2025-10-04
│
└─ Success Message: "Tenant 'Acme Corporation' created successfully!"

                              ↓

┌─────────────────────────────────────────────────────────────────────┐
│                    TENANT ADMIN WORKFLOW                            │
└─────────────────────────────────────────────────────────────────────┘

Step 6: Tenant Admin Login (First Time)
├─ URL: https://localhost:8443/t/acme-corp/Login
├─ Credentials:
│  ├─ Email:    admin@acme-corp.com
│  └─ Password: SecurePass123!
└─ Auto-redirect after login

                              ↓

Step 7: Access Tenant Admin UI
├─ URL: https://localhost:8443/t/acme-corp/Admin/Users
│
├─ Available Routes (Tenant-Specific):
│  ├─ /t/acme-corp/Admin/Users         (Manage users)
│  ├─ /t/acme-corp/Admin/Clients       (Manage OAuth clients)
│  ├─ /t/acme-corp/Admin/Consents      (User consents)
│  ├─ /t/acme-corp/Admin/Keys          (Signing keys)
│  └─ ... (all Admin pages)
│
├─ Fallback Routes (Default Tenant):
│  ├─ /Admin/Users         → Maps to default tenant
│  ├─ /Admin/Clients       → Maps to default tenant
│  └─ ... (if acme-corp is default)
│
└─ Tenant Isolation:
   ├─ ✅ Can only see acme-corp users
   ├─ ✅ Can only see acme-corp clients
   ├─ ✅ Cannot access globex-inc data
   └─ ✅ Cannot access Platform Admin UI

```

---

## Quick Start Example

### Create Tenant "Acme Corp"

```bash
# 1. Login as Platform Admin
URL: https://localhost:8443/PlatformAdmin/Tenants

# 2. Click [+ Create Tenant]

# 3. Fill form:
Slug:          acme-corp
Name:          Acme Corporation
Description:   Industrial supply company
Admin Email:   admin@acme-corp.com
Admin Pass:    SecurePass123!
Status:        Active
Billing Plan:  Pro
Max Users:     10000
Max Clients:   100

# 4. Click [Create Tenant]

# 5. Result:
✅ Tenant created successfully!
   Issuer:  https://localhost:8443/t/acme-corp
   Admin:   admin@acme-corp.com
```

### Login as Tenant Admin

```bash
# 1. Navigate to tenant-specific login
URL: https://localhost:8443/t/acme-corp/Login

# 2. Enter credentials:
Email:    admin@acme-corp.com
Password: SecurePass123!

# 3. Auto-redirected to:
URL: https://localhost:8443/t/acme-corp/Admin/Users

# 4. Can now manage:
- Users in acme-corp
- Clients in acme-corp
- Consents in acme-corp
- Keys for acme-corp
```

---

## Data Isolation Per Tenant

```
┌────────────────────────────────────────────────────────────────┐
│                         TENANT: acme-corp                      │
├────────────────────────────────────────────────────────────────┤
│ Issuer:    https://localhost:8443/t/acme-corp                 │
│ JWKS:      https://localhost:8443/t/acme-corp/.well-known/jwks│
│ Discovery: https://localhost:8443/t/acme-corp/.well-known/    │
│            openid-configuration                                │
│                                                                │
│ Users:     admin@acme-corp.com (+ others)                     │
│ Clients:   (OAuth apps for acme-corp)                         │
│ Keys:      (Separate signing keys)                            │
│ Tokens:    (Issued with acme-corp issuer claim)              │
└────────────────────────────────────────────────────────────────┘

                              ⚠️ ISOLATED ⚠️
                     (Cannot access each other's data)

┌────────────────────────────────────────────────────────────────┐
│                         TENANT: globex-inc                     │
├────────────────────────────────────────────────────────────────┤
│ Issuer:    https://localhost:8443/t/globex-inc                │
│ JWKS:      https://localhost:8443/t/globex-inc/.well-known/jwks│
│ Discovery: https://localhost:8443/t/globex-inc/.well-known/   │
│            openid-configuration                                │
│                                                                │
│ Users:     admin@globex-inc.com (+ others)                    │
│ Clients:   (OAuth apps for globex-inc)                        │
│ Keys:      (Separate signing keys)                            │
│ Tokens:    (Issued with globex-inc issuer claim)             │
└────────────────────────────────────────────────────────────────┘
```

---

## Authorization Matrix

| User Type | Can Access | Cannot Access |
|-----------|-----------|---------------|
| **Platform Admin** | `/PlatformAdmin/*`<br>Create/Edit all tenants<br>View all tenant data | Tenant-specific operations (must switch context) |
| **Tenant Admin (acme-corp)** | `/t/acme-corp/Admin/*`<br>Manage acme-corp users<br>Manage acme-corp clients<br>View acme-corp data | `/PlatformAdmin/*`<br>globex-inc data<br>Other tenant data |
| **Tenant Admin (globex-inc)** | `/t/globex-inc/Admin/*`<br>Manage globex-inc users<br>Manage globex-inc clients<br>View globex-inc data | `/PlatformAdmin/*`<br>acme-corp data<br>Other tenant data |
| **Regular User** | `/Account/*` (self-service)<br>Consent management<br>Profile updates | `/Admin/*` (any tenant)<br>`/PlatformAdmin/*`<br>Other user data |

---

## Key Endpoints by Tenant

### Acme Corp (`acme-corp`)

| Endpoint Type | URL |
|---------------|-----|
| **Login** | `https://localhost:8443/t/acme-corp/Login` |
| **Admin UI** | `https://localhost:8443/t/acme-corp/Admin/*` |
| **Discovery** | `https://localhost:8443/t/acme-corp/.well-known/openid-configuration` |
| **JWKS** | `https://localhost:8443/t/acme-corp/.well-known/jwks` |
| **Authorize** | `https://localhost:8443/t/acme-corp/connect/authorize` |
| **Token** | `https://localhost:8443/t/acme-corp/connect/token` |
| **Userinfo** | `https://localhost:8443/t/acme-corp/connect/userinfo` |

### Globex Inc (`globex-inc`)

| Endpoint Type | URL |
|---------------|-----|
| **Login** | `https://localhost:8443/t/globex-inc/Login` |
| **Admin UI** | `https://localhost:8443/t/globex-inc/Admin/*` |
| **Discovery** | `https://localhost:8443/t/globex-inc/.well-known/openid-configuration` |
| **JWKS** | `https://localhost:8443/t/globex-inc/.well-known/jwks` |
| **Authorize** | `https://localhost:8443/t/globex-inc/connect/authorize` |
| **Token** | `https://localhost:8443/t/globex-inc/connect/token` |
| **Userinfo** | `https://localhost:8443/t/globex-inc/connect/userinfo` |

---

## Form Field Reference

### Slug (Required)
- **Format**: Lowercase letters, numbers, hyphens only
- **Examples**: ✅ `acme-corp`, `tenant-123`, `my-company`
- **Invalid**: ❌ `Acme Corp`, `acme_corp`, `acme corp`
- **Used in**: URLs, issuer URIs, database keys

### Name (Required)
- **Format**: Any string (max 200 chars)
- **Examples**: `Acme Corporation`, `Globex Inc.`, `Stark Industries`
- **Used in**: UI display, emails, reports

### Admin Email (Required)
- **Format**: Valid email address
- **Validation**: Must be unique across ALL tenants
- **Used in**: First admin user account

### Admin Password (Required)
- **Format**: Minimum 8 characters
- **Security**: Hashed with Argon2id (memory-hard algorithm)
- **Used in**: First admin user authentication

### Status (Optional)
- **Options**:
  - `Active` - Tenant is ready for use
  - `Pending Setup` - Initial configuration needed
  - `Suspended` - Temporarily disabled
  - `Deleted` - Soft delete
- **Default**: `Active`

### Billing Plan (Optional)
- **Options**: `Free`, `Starter`, `Pro`, `Enterprise`
- **Default**: `Free`
- **Used in**: Billing/usage tracking

### Max Users / Max Clients (Optional)
- **Format**: Integer (0 = unlimited)
- **Default**: Max Users = 10000, Max Clients = 100
- **Used in**: Quota enforcement

### Logo URL / Colors (Optional)
- **Logo URL**: Full URL to image
- **Primary/Accent Color**: Hex format (`#RRGGBB`)
- **Used in**: Tenant-specific branding

---

## Common Scenarios

### Scenario 1: SaaS Provider Creating Customer Tenants
```
Platform Admin creates:
- Tenant A: customer-acme (Acme Corp's isolated instance)
- Tenant B: customer-globex (Globex Inc's isolated instance)

Each customer gets:
- Unique issuer URI
- Separate signing keys
- Isolated user/client databases
- Custom branding
```

### Scenario 2: Enterprise Multi-Department Setup
```
Platform Admin creates:
- Tenant A: dept-sales (Sales department)
- Tenant B: dept-eng (Engineering department)
- Tenant C: dept-hr (HR department)

Each department gets:
- Own admin to manage users
- Own OAuth clients for apps
- Isolated consent/token data
```

### Scenario 3: Development/Staging/Production
```
Platform Admin creates:
- Tenant A: dev-env (Development)
- Tenant B: staging-env (Staging)
- Tenant C: prod-env (Production)

Each environment gets:
- Separate configuration
- Independent user databases
- Isolated testing
```

---

## Summary

**UI Flow**:
1. Platform Admin → Login → Platform Admin UI
2. Click [+ Create Tenant]
3. Fill form (slug, name, admin email/password)
4. Submit → Tenant created with issuer URI, default realm, first admin
5. Tenant Admin → Login at `/t/{slug}/Login`
6. Access Tenant Admin UI at `/t/{slug}/Admin/*`

**Key Features**:
- ✅ Single form creates: Tenant + Realm + Admin User
- ✅ Auto-generated issuer URI (multi-tenant or single-tenant mode)
- ✅ Password hashed with Argon2id
- ✅ Email auto-verified for first admin
- ✅ Optional branding (logo, colors)
- ✅ Quota limits (max users/clients)
- ✅ Complete data isolation per tenant

**Next**: Login as tenant admin and manage users/clients!
