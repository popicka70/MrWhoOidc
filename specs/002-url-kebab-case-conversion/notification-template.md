# URL Convention Migration Notification Template

**Subject**: [Action Required] MrWhoOidc URL Convention Change - 30 Day Notice

---

## Email Body

Dear [Recipient Name],

We are writing to inform you of an upcoming change to MrWhoOidc that will affect your integration with our OpenID Connect Provider (OP).

### What's Changing

**Effective Date**: [Deployment Date - Day 30]

MrWhoOidc is standardizing all HTTP endpoints to use **kebab-case** URL convention. This is a breaking change that requires configuration updates on your end.

### URL Changes

All endpoint URLs containing PascalCase segments will change to kebab-case. Below are the mappings relevant to your integration:

#### Core Protocol Endpoints

| Old URL (PascalCase) | New URL (kebab-case) |
|----------------------|----------------------|
| `/Auth/External/Start` | `/auth/external/start` |
| `/Auth/External/Callback` | `/auth/external/callback` |
| `/Auth/External/Confirm` | `/auth/external/confirm` |
| `/Auth/QrMobile` | `/auth/qr-mobile` |

#### Admin UI Endpoints (if applicable)

| Old URL (PascalCase) | New URL (kebab-case) |
|----------------------|----------------------|
| `/Admin/Providers` | `/admin/providers` |
| `/Admin/Clients` | `/admin/clients` |
| `/Admin/Users` | `/admin/users` |
| `/Admin/Tenants` | `/admin/tenants` |
| `/Admin/Realms` | `/admin/realms` |
| `/PlatformAdmin/Settings` | `/platform-admin/settings` |

#### User-Facing Pages (if applicable)

| Old URL (PascalCase) | New URL (kebab-case) |
|----------------------|----------------------|
| `/Account/Manage` | `/account/manage` |
| `/Account/Profile` | `/account/profile` |
| `/Account/Security` | `/account/security` |

### Discovery Document Update

Our OpenID Connect discovery document (`/.well-known/openid-configuration`) will be updated to reflect the new kebab-case endpoints. If you rely on discovery for endpoint resolution, your integration may automatically adapt.

**Discovery URL (unchanged)**: `https://[your-op-domain]/.well-known/openid-configuration`

### Action Required

**If you are an External Identity Provider (IdP)**:
- Update your registered callback URL for MrWhoOidc:
  - Old: `https://[your-op-domain]/Auth/External/Callback`
  - New: `https://[your-op-domain]/auth/external/callback`
- Update any deep links or bookmarks to MrWhoOidc admin UI

**If you are a Relying Party (RP) Client**:
- If you use discovery-based configuration, verify your client library fetches updated endpoint URLs from discovery document
- If you hardcode endpoint URLs, update them to kebab-case convention
- Update any bookmarks or documentation referencing MrWhoOidc URLs

### Timeline

- **Day 0 (Today)**: Initial notification sent
- **Day 7**: Reminder notification
- **Day 14**: Reminder notification
- **Day 21**: Reminder notification
- **Day 28**: Final reminder (48 hours before deployment)
- **Day 30**: Migration deployed to production
- **Day 30+**: Old PascalCase URLs return 404 with helpful error message suggesting kebab-case alternative

### Testing

We recommend testing your integration in our **staging environment** before the production deployment:

**Staging OP Base URL**: `https://[staging-domain]`
**Staging Discovery**: `https://[staging-domain]/.well-known/openid-configuration`

Staging will be updated with kebab-case endpoints on **[Staging Date - Day 20]**, giving you 10 days to test before production deployment.

### Support

If you have questions or need assistance with this migration, please contact:

- **Email**: [support-email]
- **Support Portal**: [support-url]
- **Migration FAQ**: [faq-url]

### Why This Change?

Standardizing on kebab-case improves:
- URL readability and consistency
- Alignment with web standards and best practices
- Integration with modern frameworks and tooling

We apologize for any inconvenience and appreciate your cooperation with this migration.

Thank you,  
The MrWhoOidc Team

---

## Reminder Email (Day 7, 14, 21)

**Subject**: [Reminder] MrWhoOidc URL Convention Change - [Days Remaining] Days Until Deployment

Dear [Recipient Name],

This is a reminder that MrWhoOidc will migrate to kebab-case URL convention in **[Days Remaining] days** (on **[Deployment Date]**).

### Quick Checklist

- [ ] Updated callback URLs in your IdP configuration
- [ ] Tested integration against staging environment
- [ ] Updated hardcoded endpoint URLs (if any)
- [ ] Updated documentation and bookmarks

**Staging Available**: Our staging environment is ready for testing at `https://[staging-domain]`.

If you have not yet taken action, please refer to our initial notification email (sent on [Day 0 Date]) for detailed migration instructions.

Need help? Contact us at [support-email].

Thank you,  
The MrWhoOidc Team

---

## Final Reminder Email (Day 28)

**Subject**: [Final Notice] MrWhoOidc URL Convention Change - Deployment in 48 Hours

Dear [Recipient Name],

This is your **final reminder** that MrWhoOidc will migrate to kebab-case URL convention in **48 hours** (on **[Deployment Date]**).

### Critical Action Required

If you have not yet updated your configuration, please do so immediately:

1. **External IdPs**: Update callback URL to `https://[your-op-domain]/auth/external/callback`
2. **RP Clients**: Verify discovery-based configuration or update hardcoded URLs
3. **All Users**: Test integration against staging environment

**After deployment**, all PascalCase URLs will return 404 errors.

### Emergency Support

If you encounter issues after deployment, contact our support team immediately:

- **Emergency Hotline**: [emergency-phone]
- **Email**: [support-email]
- **Priority Support Portal**: [support-url]

Thank you for your cooperation.

The MrWhoOidc Team

---

## Post-Deployment Follow-Up (Day 31)

**Subject**: MrWhoOidc URL Convention Migration - Deployment Complete

Dear [Recipient Name],

The MrWhoOidc URL convention migration was successfully deployed to production on **[Deployment Date]**.

### Migration Status

All HTTP endpoints now use kebab-case convention. PascalCase URLs return 404 errors with helpful suggestions.

### Need Help?

If you experience integration issues:

1. Verify your configuration uses kebab-case URLs
2. Check discovery document: `https://[your-op-domain]/.well-known/openid-configuration`
3. Review error messages for specific guidance
4. Contact support: [support-email]

### Monitoring

We are actively monitoring integration health and 404 rates. If you observe unexpected behavior, please report it immediately.

Thank you for your partnership during this migration.

The MrWhoOidc Team

---

## Contact List Generation Queries

### External IdP Contacts

```sql
-- Query to extract IdP admin contact information
SELECT 
    ip.Id AS ProviderId,
    ip.Name AS ProviderName,
    ip.AdminEmail,
    ip.AdminName,
    t.Slug AS TenantSlug,
    t.Name AS TenantName
FROM IdentityProviders ip
INNER JOIN Tenants t ON ip.TenantId = t.Id
WHERE ip.AdminEmail IS NOT NULL
ORDER BY t.Slug, ip.Name;
```

### RP Client Contacts

```sql
-- Query to extract RP client contact information
SELECT 
    c.Id AS ClientId,
    c.ClientId AS ClientIdentifier,
    c.ClientName,
    c.ContactEmail,
    c.ContactName,
    t.Slug AS TenantSlug,
    t.Name AS TenantName
FROM Clients c
INNER JOIN Tenants t ON c.TenantId = t.Id
WHERE c.ContactEmail IS NOT NULL
ORDER BY t.Slug, c.ClientName;
```

### Admin User Contacts

```sql
-- Query to extract tenant admin contacts
SELECT DISTINCT
    u.Email,
    u.UserName,
    t.Slug AS TenantSlug,
    t.Name AS TenantName
FROM Users u
INNER JOIN TenantAssignments ta ON u.Id = ta.UserId
INNER JOIN Tenants t ON ta.TenantId = t.Id
WHERE ta.IsAdmin = true
ORDER BY t.Slug, u.Email;
```

---

## Notification Tracking Template

| Contact Type | Contact Name | Email | Tenant | Day 0 Sent | Day 7 Sent | Day 14 Sent | Day 21 Sent | Day 28 Sent | Day 31 Sent | Notes |
|--------------|--------------|-------|--------|------------|------------|-------------|-------------|-------------|-------------|-------|
| IdP | [Name] | [Email] | [Tenant] | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | |
| RP | [Name] | [Email] | [Tenant] | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | |
| Admin | [Name] | [Email] | [Tenant] | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | |

---

## FAQ for Recipients

**Q: Will the discovery document be updated automatically?**  
A: Yes, `/.well-known/openid-configuration` will reflect new kebab-case endpoints on deployment day.

**Q: Can I use both old and new URLs during transition?**  
A: No. This is a clean break migration. Only kebab-case URLs will work after Day 30.

**Q: What if I miss the deadline?**  
A: Old URLs will return 404 errors with helpful suggestions. Update your configuration and re-test immediately.

**Q: How do I test before production deployment?**  
A: Use our staging environment at `https://[staging-domain]`, available from Day 20 onwards.

**Q: Will this affect my existing user sessions?**  
A: No. Active sessions remain valid. Only endpoint URLs change.

**Q: What about deep links in emails?**  
A: Email confirmation links generated before Day 30 will become invalid. Users should request new confirmation emails.

**Q: Do I need to update my client credentials?**  
A: No. Only URLs change. Client IDs, secrets, and scopes remain unchanged.

**Q: Will this affect JWKS or token validation?**  
A: No. Token structure, signing keys, and validation logic remain unchanged.
