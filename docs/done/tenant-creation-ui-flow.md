# Tenant Creation UI Flow

## Overview

The tenant creation flow is designed for **Platform Administrators** who need to create and manage isolated tenant instances in the multi-tenant OIDC server. This document describes the complete UI flow and what happens behind the scenes.

---

## User Persona

**Platform Administrator**
- Has the `platform-admin` role
- Can access Platform Admin UI at `/PlatformAdmin/*`
- Manages all tenants across the entire system
- Typically internal IT staff or SaaS platform operators

---

## UI Flow (Step-by-Step)

### Step 1: Access Platform Admin UI

**URL**: `https://localhost:8443/PlatformAdmin/Tenants`

**Authentication Required**: Yes (must have `platform-admin` policy claim)

**What You See**:
```
╔════════════════════════════════════════════════════════════════╗
║ 🏢 Tenants                          [+ Create Tenant] Button ║
║ Manage multi-tenant instances                                  ║
╠════════════════════════════════════════════════════════════════╣
║                                                                ║
║ ℹ️ No tenants found. Create your first tenant to get started. ║
║                                                                ║
╠════════════════════════════════════════════════════════════════╣
║ ℹ️ Multi-Tenancy Information                                  ║
║                                                                ║
║ Current Mode: [MultiTenant]                                   ║
║ Tenants are accessed via /t/{slug}/* URLs                     ║
║                                                                ║
║ Default Tenant: default                                       ║
║ Fallback tenant for non-prefixed routes                       ║
╚════════════════════════════════════════════════════════════════╝
```

**Actions Available**:
- Click **[+ Create Tenant]** button → Navigate to Create page

---

### Step 2: Click "Create Tenant" Button

**Navigates to**: `/PlatformAdmin/Tenants/Create`

**What You See**: A comprehensive form with 4 sections:

```
╔════════════════════════════════════════════════════════════════╗
║ ➕ Create Tenant                     [← Back to List] Button ║
║ Set up a new tenant instance                                   ║
╠════════════════════════════════════════════════════════════════╣
║                                                                ║
║ ℹ️ Basic Information                                          ║
║ ┌─────────────────────────────┬─────────────────────────────┐ ║
║ │ Slug *                      │ Name *                      │ ║
║ │ [acme-corp____________]     │ [Acme Corporation______]   │ ║
║ │ URL-safe identifier         │ Display name for tenant    │ ║
║ │ Used in URLs: /t/{slug}     │                            │ ║
║ └─────────────────────────────┴─────────────────────────────┘ ║
║                                                                ║
║ Description (optional)                                         ║
║ [_____________________________________________________]        ║
║                                                                ║
║ ┌─────────────────────────────┬─────────────────────────────┐ ║
║ │ Admin Email *               │ Admin Password *            │ ║
║ │ [admin@acme-corp.com__]    │ [••••••••______________]   │ ║
║ │ First admin user email      │ Min 8 characters            │ ║
║ └─────────────────────────────┴─────────────────────────────┘ ║
║                                                                ║
╠════════════════════════════════════════════════════════════════╣
║ ⚙️ Configuration                                              ║
║ ┌─────────────────────────────┬─────────────────────────────┐ ║
║ │ Status                      │ Billing Plan                │ ║
║ │ [Active - Ready ▼]         │ [Free ▼]                   │ ║
║ │                             │ • Free / Starter / Pro /    │ ║
║ │                             │   Enterprise                │ ║
║ └─────────────────────────────┴─────────────────────────────┘ ║
║                                                                ║
║ ┌─────────────────────────────┬─────────────────────────────┐ ║
║ │ Max Users                   │ Max Clients                 │ ║
║ │ [10000_______________]     │ [100_________________]     │ ║
║ │ 0 = unlimited               │ 0 = unlimited               │ ║
║ └─────────────────────────────┴─────────────────────────────┘ ║
║                                                                ║
╠════════════════════════════════════════════════════════════════╣
║ 🎨 Branding (Optional)                                        ║
║                                                                ║
║ Logo URL                                                       ║
║ [https://example.com/logo.png___________________________]     ║
║                                                                ║
║ ┌─────────────────────────────┬─────────────────────────────┐ ║
║ │ Primary Color               │ Accent Color                │ ║
║ │ [🎨 #0d6efd]               │ [🎨 #6610f2]               │ ║
║ │ Main brand color            │ Secondary brand color       │ ║
║ └─────────────────────────────┴─────────────────────────────┘ ║
║                                                                ║
╠════════════════════════════════════════════════════════════════╣
║ [❌ Cancel]                              [✅ Create Tenant] ║
╚════════════════════════════════════════════════════════════════╝
```

---

### Step 3: Fill Out Form

**Required Fields** (marked with *):
- **Slug**: URL-safe identifier (lowercase, hyphens only)
  - Example: `acme-corp`, `globex-inc`, `stark-industries`
  - Used in URLs: `https://localhost:8443/t/acme-corp/`
  - Validation: Must be unique, lowercase letters/numbers/hyphens only

- **Name**: Display name for the tenant
  - Example: `Acme Corporation`, `Globex Inc.`, `Stark Industries`
  - Shown in UI, emails, and admin interfaces

- **Admin Email**: Email for first admin user
  - Example: `admin@acme-corp.com`
  - Validation: Must be valid email, unique across all tenants
  - This user is created automatically with tenant

- **Admin Password**: Password for first admin user
  - Minimum 8 characters
  - Hashed with Argon2id before storage

**Optional Fields**:
- **Description**: Brief description of tenant
- **Status**: Initial status (Active or Pending Setup)
- **Billing Plan**: Free, Starter, Pro, or Enterprise
- **Max Users**: Limit number of users (0 = unlimited)
- **Max Clients**: Limit number of OAuth clients (0 = unlimited)
- **Logo URL**: URL to tenant logo image
- **Primary Color**: Main brand color (hex format)
- **Accent Color**: Secondary brand color (hex format)

---

### Step 4: Submit Form

**Action**: Click **[✅ Create Tenant]** button

**What Happens Behind the Scenes**:

1. **Validation** (Client-side + Server-side):
   - Slug format validation (lowercase, hyphens only)
   - Slug uniqueness check
   - Email format validation
   - Email uniqueness check (across all tenants)
   - Password strength check (min 8 chars)
   - Color format validation (hex colors)

2. **Database Operations** (in transaction):
   ```sql
   -- Create tenant record
   INSERT INTO Tenants (Slug, Name, IssuerUri, Status, AdminEmail, ...)
   VALUES ('acme-corp', 'Acme Corporation', 
           'https://localhost:8443/t/acme-corp', 'Active', ...);

   -- Create default realm for tenant
   INSERT INTO Realms (TenantId, Name, DisplayName)
   VALUES (<tenant-id>, 'default', 'Acme Corporation Default Realm');

   -- Create first admin user
   INSERT INTO Users (TenantId, Username, Email, PasswordHash, EmailVerified)
   VALUES (<tenant-id>, 'admin', 'admin@acme-corp.com', 
           '<argon2id-hash>', true);
   ```

3. **Issuer URI Generation**:
   - **Multi-tenant mode**: `https://localhost:8443/t/{slug}`
   - **Single-tenant mode**: `https://localhost:8443`

4. **Success Actions**:
   - Redirect to tenant list page
   - Show success message: `"Tenant 'Acme Corporation' created successfully! Admin user: admin@acme-corp.com"`

---

### Step 5: View Tenant List (After Creation)

**URL**: `https://localhost:8443/PlatformAdmin/Tenants`

**What You See**:
```
╔═══════════════════════════════════════════════════════════════════════════════════════╗
║ 🏢 Tenants                                          [+ Create Tenant] Button         ║
║ Manage multi-tenant instances                                                         ║
╠═══════════════════════════════════════════════════════════════════════════════════════╣
║ ✅ Tenant 'Acme Corporation' created successfully! Admin user: admin@acme-corp.com   ║
╠═══════════════════════════════════════════════════════════════════════════════════════╣
║ Slug        │ Name              │ Issuer                            │ Status  │ Users ║
║─────────────┼───────────────────┼───────────────────────────────────┼─────────┼───────║
║ acme-corp   │ Acme Corporation  │ https://localhost:8443/t/acme... │ ✅Active│ 1/10K ║
║             │ Industrial supply │                                   │         │       ║
║─────────────┼───────────────────┼───────────────────────────────────┼─────────┼───────║
║             │                                                                   [Edit] ║
╚═══════════════════════════════════════════════════════════════════════════════════════╝

Full table columns:
- Slug (code format, primary identifier)
- Name (bold with optional description below)
- Issuer URI (full issuer URL)
- Status (Active/Suspended/Pending/Deleted badge)
- Users (count with max limit)
- Clients (count with max limit)
- Created (date)
- Actions (Edit button)
```

---

## Accessing Tenant-Specific Admin UI

Once a tenant is created, the **tenant admin user** can log in and access their **Tenant Admin UI**:

### Multi-Tenant Mode

**Tenant-Specific Route** (recommended):
```
https://localhost:8443/t/acme-corp/Login
  → Login as admin@acme-corp.com
  → Redirect to: https://localhost:8443/t/acme-corp/Admin/Users
```

**Fallback Route** (uses default tenant):
```
https://localhost:8443/Login
  → Login as admin@acme-corp.com (if acme-corp is default)
  → Redirect to: https://localhost:8443/Admin/Users
```

### Single-Tenant Mode

**Root-Level Route** (only available route):
```
https://localhost:8443/Login
  → Login as admin@acme-corp.com
  → Redirect to: https://localhost:8443/Admin/Users
```

---

## Tenant Admin vs Platform Admin

| Feature | Platform Admin | Tenant Admin |
|---------|---------------|--------------|
| **Access** | `/PlatformAdmin/*` | `/Admin/*` or `/t/{slug}/Admin/*` |
| **Create Tenants** | ✅ Yes | ❌ No |
| **Manage All Tenants** | ✅ Yes | ❌ No (only their tenant) |
| **Create Users** | ✅ In any tenant | ✅ Only in their tenant |
| **Create Clients** | ✅ In any tenant | ✅ Only in their tenant |
| **View Billing** | ✅ All tenants | ✅ Only their tenant |
| **Edit Tenant Settings** | ✅ All tenants | ❌ No (request via Platform Admin) |

---

## Data Isolation After Creation

Once a tenant is created, **all data is isolated**:

### Database Level
```sql
-- All queries are tenant-scoped via TenantId filter
SELECT * FROM Users WHERE TenantId = <tenant-id>;
SELECT * FROM Clients WHERE TenantId = <tenant-id>;
SELECT * FROM Tokens WHERE TenantId = <tenant-id>;
```

### Issuer URI
- **Tenant A**: `https://localhost:8443/t/acme-corp`
- **Tenant B**: `https://localhost:8443/t/globex-inc`
- Tokens issued for Tenant A will **not validate** for Tenant B

### JWKS Endpoint
- **Tenant A JWKS**: `https://localhost:8443/t/acme-corp/.well-known/jwks`
- **Tenant B JWKS**: `https://localhost:8443/t/globex-inc/.well-known/jwks`
- Each tenant has **separate signing keys**

### Discovery Endpoint
- **Tenant A**: `https://localhost:8443/t/acme-corp/.well-known/openid-configuration`
- **Tenant B**: `https://localhost:8443/t/globex-inc/.well-known/openid-configuration`
- Issuer claim differs per tenant

---

## Example: Creating Two Tenants

### Tenant 1: Acme Corp
```
Slug:          acme-corp
Name:          Acme Corporation
Description:   Industrial supply company
Admin Email:   admin@acme-corp.com
Admin Pass:    SecurePass123!
Status:        Active
Billing Plan:  Pro
Max Users:     10000
Max Clients:   100

Generated:
  Issuer:  https://localhost:8443/t/acme-corp
  Admin:   admin@acme-corp.com (auto-created)
  Realm:   default (auto-created)
```

### Tenant 2: Globex Inc
```
Slug:          globex-inc
Name:          Globex Inc
Description:   Global technology services
Admin Email:   admin@globex-inc.com
Admin Pass:    GlobalPass456!
Status:        Active
Billing Plan:  Enterprise
Max Users:     50000
Max Clients:   500

Generated:
  Issuer:  https://localhost:8443/t/globex-inc
  Admin:   admin@globex-inc.com (auto-created)
  Realm:   default (auto-created)
```

### Access After Creation

**Acme Corp Admin Login**:
```
1. Navigate to: https://localhost:8443/t/acme-corp/Login
2. Enter: admin@acme-corp.com / SecurePass123!
3. Redirected to: https://localhost:8443/t/acme-corp/Admin/Users
4. Can manage:
   - Users in acme-corp tenant only
   - Clients in acme-corp tenant only
   - Consents in acme-corp tenant only
```

**Globex Inc Admin Login**:
```
1. Navigate to: https://localhost:8443/t/globex-inc/Login
2. Enter: admin@globex-inc.com / GlobalPass456!
3. Redirected to: https://localhost:8443/t/globex-inc/Admin/Users
4. Can manage:
   - Users in globex-inc tenant only
   - Clients in globex-inc tenant only
   - Consents in globex-inc tenant only
```

**Platform Admin**:
```
1. Navigate to: https://localhost:8443/PlatformAdmin/Tenants
2. Can see both tenants in list
3. Can edit both tenants
4. Can create new tenants
```

---

## Validation Rules

### Slug Validation
- ✅ Valid: `acme-corp`, `globex-inc-2024`, `tenant-1`
- ❌ Invalid: `Acme Corp` (uppercase), `acme_corp` (underscore), `acme corp` (space)
- ❌ Duplicate: Must be unique across all tenants

### Email Validation
- ✅ Valid: `admin@acme-corp.com`, `user@example.com`
- ❌ Invalid: `not-an-email`, `missing@domain`
- ❌ Duplicate: Must be unique across **all tenants** (global uniqueness)

### Password Validation
- ✅ Valid: 8+ characters
- ❌ Invalid: Less than 8 characters

### Color Validation
- ✅ Valid: `#0d6efd`, `#ff5733`, `#ABCDEF`
- ❌ Invalid: `blue`, `rgb(13,110,253)`, `#zzz`

---

## Behind the Scenes: Database Schema

### Tables Created/Updated

```sql
-- Tenants table
CREATE TABLE Tenants (
    Id UUID PRIMARY KEY,
    Slug VARCHAR(100) UNIQUE NOT NULL,
    Name VARCHAR(200) NOT NULL,
    Description VARCHAR(500),
    IssuerUri VARCHAR(500) NOT NULL,
    Status INTEGER NOT NULL, -- 1=Active, 2=Suspended, 3=PendingSetup, 4=Deleted
    MaxUsers INTEGER NOT NULL DEFAULT 10000,
    MaxClients INTEGER NOT NULL DEFAULT 100,
    LogoUrl VARCHAR(200),
    PrimaryColor VARCHAR(50),
    AccentColor VARCHAR(50),
    AdminEmail VARCHAR(256) NOT NULL,
    BillingPlan VARCHAR(100),
    CreatedAt TIMESTAMP NOT NULL
);

-- Realms table (one default realm per tenant)
CREATE TABLE Realms (
    Id UUID PRIMARY KEY,
    TenantId UUID NOT NULL REFERENCES Tenants(Id),
    Name VARCHAR(100) NOT NULL,
    DisplayName VARCHAR(200)
);

-- Users table (first admin user)
CREATE TABLE Users (
    Id UUID PRIMARY KEY,
    TenantId UUID NOT NULL REFERENCES Tenants(Id),
    Username VARCHAR(256) NOT NULL,
    Email VARCHAR(256) NOT NULL,
    NormalizedEmail VARCHAR(256) NOT NULL,
    PasswordHash VARCHAR(500) NOT NULL,
    EmailVerified BOOLEAN NOT NULL DEFAULT FALSE,
    CreatedAt TIMESTAMP NOT NULL
);
```

---

## Security Considerations

1. **Password Hashing**: Admin password hashed with **Argon2id** (memory-hard algorithm)
2. **Email Verification**: First admin user has `EmailVerified = true` (auto-verified)
3. **Authorization**: Platform Admin UI requires `platform-admin` policy claim
4. **Slug Validation**: Prevents injection attacks (only lowercase/numbers/hyphens)
5. **Issuer Isolation**: Each tenant has unique issuer URI for token validation

---

## Next Steps After Tenant Creation

1. **Login as Tenant Admin**: Use admin email/password created during setup
2. **Configure Client Applications**: Create OAuth clients for your apps
3. **Add Users**: Create additional users in Tenant Admin UI
4. **Customize Branding**: Update logo, colors via Platform Admin edit page
5. **Test OIDC Flows**: Verify authorization, token, userinfo endpoints work

---

## Troubleshooting

### "Tenant with this slug already exists"
**Cause**: Slug is not unique  
**Solution**: Choose a different slug

### "This email is already in use"
**Cause**: Email already exists in another tenant  
**Solution**: Use a different email address

### Cannot access tenant-specific routes
**Cause**: Multi-tenancy not enabled  
**Solution**: Set `MultiTenancy__Enabled: true` in configuration

### Tenant admin cannot access Platform Admin UI
**Cause**: User does not have `platform-admin` policy claim  
**Solution**: This is intentional - only platform admins can access `/PlatformAdmin/*`

---

## Related Documentation

- **Multi-Tenancy Backlog**: `/docs/multitenancy-backlog.md`
- **Tenant Admin UI Routing**: `/docs/copilot-instructions.md` (Backchannel section)
- **Authorization Policies**: `/docs/admin-guide.md`
- **Developer Guide**: `/docs/developer-guide.md`
