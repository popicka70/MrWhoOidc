# Client Secret Rotation Guide

> **Superseded operational guidance.** Use [Client Secret Rotation](../for-operators/client-secret-rotation.md). This archived guide preserves earlier design and UI details; its limits, timing, and commands are not a current runbook.

**Audience**: Application developers and client administrators  
**Version**: 1.0  
**Date**: October 17, 2025

---

## Overview

This guide explains how to safely rotate your OAuth 2.0 / OpenID Connect client secrets in MrWhoOidc without causing authentication downtime. Client secrets are used by confidential clients (typically server-side applications) to authenticate with the authorization server.

### When to Rotate Secrets

You should rotate client secrets:

- **Regularly**: Every 90 days as a security best practice (or per your organization's policy)
- **When compromised**: Immediately if you suspect a secret has been exposed
- **Before expiry**: At least 7 days before the current secret expires (you'll receive notifications)
- **After team changes**: When personnel with access to secrets leave your team

### Benefits of Rotation

- **Zero downtime**: MrWhoOidc supports multiple active secrets, allowing gradual rollover
- **Security**: Limits the window of exposure if a secret is compromised
- **Compliance**: Meets regulatory requirements for periodic credential rotation

---

## Prerequisites

Before you begin, ensure you have:

1. **Admin access** to the MrWhoOidc Admin UI or API
2. **Deployment access** to your client application's configuration
3. **Monitoring** to verify successful authentication after rotation
4. **Rollback plan** (ability to revert your application config if needed)

---

## Rotation Workflow

The recommended approach uses an **overlap period** where both old and new secrets are valid simultaneously. This allows you to update your application configuration without service interruption.

### Step 1: Generate New Secret

**Via Admin UI:**

1. Navigate to **Admin → Clients → [Your Client] → Secrets**
2. Click **"Add Secret"** button
3. Enter a description (e.g., "Q4 2025 Production Secret")
4. Set expiry (optional, default = 90 days from activation)
5. Leave **"Activate immediately"** unchecked (we'll activate after deployment)
6. Click **Generate**

**Via Admin API:**

```http
POST /api/admin/clients/{clientId}/secrets
Content-Type: application/json
Authorization: Bearer {your-admin-token}

{
  "description": "Q4 2025 Production Secret",
  "expiresInDays": 90,
  "activateImmediately": false
}
```

**Response:**

```json
{
  "secretId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "secretValue": "MrWho_K8sD3f9JpQz7Yh2NmXvL4cR6tU0wE5gA1bN9iO8",
  "expiresAtUtc": "2026-01-14T12:00:00Z",
  "warning": "Save this secret now. It will not be shown again."
}
```

⚠️ **CRITICAL**: Copy the `secretValue` immediately. This is the ONLY time you'll see it. Store it securely (e.g., Azure Key Vault, AWS Secrets Manager, HashiCorp Vault).

---

### Step 2: Update Application Configuration

Update your client application to use the new secret. The specific steps depend on your tech stack:

#### .NET (ASP.NET Core)

**appsettings.json** or **Azure App Configuration**:

```json
{
  "Authentication": {
    "MrWhoOidc": {
      "Authority": "https://auth.example.com",
      "ClientId": "my-client-id",
      "ClientSecret": "MrWho_K8sD3f9JpQz7Yh2NmXvL4cR6tU0wE5gA1bN9iO8"
    }
  }
}
```

**Startup.cs / Program.cs**:

```csharp
services.AddAuthentication(options => { /* ... */ })
    .AddOpenIdConnect("MrWhoOidc", options =>
    {
        options.Authority = Configuration["Authentication:MrWhoOidc:Authority"];
        options.ClientId = Configuration["Authentication:MrWhoOidc:ClientId"];
        options.ClientSecret = Configuration["Authentication:MrWhoOidc:ClientSecret"];
        // ... other options
    });
```

#### Node.js (Passport.js / openid-client)

```javascript
const { Issuer } = require('openid-client');

const issuer = await Issuer.discover('https://auth.example.com');
const client = new issuer.Client({
  client_id: 'my-client-id',
  client_secret: process.env.OIDC_CLIENT_SECRET, // Load from env var or secret store
  redirect_uris: ['https://myapp.example.com/callback'],
  response_types: ['code'],
});
```

**Environment variable**:

```bash
export OIDC_CLIENT_SECRET="MrWho_K8sD3f9JpQz7Yh2NmXvL4cR6tU0wE5gA1bN9iO8"
```

#### Python (Authlib / python-jose)

```python
from authlib.integrations.flask_client import OAuth

oauth = OAuth(app)
oauth.register(
    name='mrwhooidc',
    client_id='my-client-id',
    client_secret=os.getenv('OIDC_CLIENT_SECRET'),  # Load from env
    server_metadata_url='https://auth.example.com/.well-known/openid-configuration',
    client_kwargs={'scope': 'openid profile email'}
)
```

#### Java (Spring Security)

**application.yml**:

```yaml
spring:
  security:
    oauth2:
      client:
        registration:
          mrwhooidc:
            client-id: my-client-id
            client-secret: ${OIDC_CLIENT_SECRET}  # From environment or config server
            scope: openid,profile,email
            redirect-uri: "{baseUrl}/login/oauth2/code/{registrationId}"
        provider:
          mrwhooidc:
            issuer-uri: https://auth.example.com
```

---

### Step 3: Deploy Updated Configuration

Deploy your application with the new secret. Use your standard deployment process:

- **Blue/Green deployment**: Deploy to new instances, test, then switch traffic
- **Rolling update**: Gradually replace instances with updated config
- **Configuration reload**: If your app supports hot-reload of config (Azure App Config, Kubernetes ConfigMaps)

**Test in non-production first!** Deploy to dev/staging environments before production.

---

### Step 4: Activate New Secret

Once your application is deployed and running with the new secret, activate it in MrWhoOidc:

**Via Admin UI:**

1. Go to **Admin → Clients → [Your Client] → Secrets**
2. Find the new secret (status: **Inactive**)
3. Click **"Activate"** button
4. Confirm activation

**Via Admin API:**

```http
POST /api/admin/clients/{clientId}/secrets/{secretId}/activate
Authorization: Bearer {your-admin-token}
```

✅ At this point, **both old and new secrets are valid**. Your application can authenticate with either one.

---

### Step 5: Set New Secret as Primary (Optional)

Mark the new secret as "primary" to indicate it's the recommended one:

**Via Admin UI:**

1. In the Secrets list, click **"Set as Primary"** on the new secret
2. This is a visual indicator only; it doesn't affect functionality

**Via Admin API:**

```http
POST /api/admin/clients/{clientId}/secrets/{secretId}/set-primary
Authorization: Bearer {your-admin-token}
```

---

### Step 6: Monitor and Verify

Before revoking the old secret, verify that:

1. **Application logs** show successful OIDC authentication
2. **No errors** related to authentication in your monitoring dashboards
3. **Traffic** is successfully flowing through your application

**Recommended monitoring period**: 24-48 hours for production applications.

Check MrWhoOidc metrics to confirm the new secret is being used:

- **Admin UI**: View "Last Used" timestamp in the Secrets table
- **Metrics**: Look for `oidc.client_secrets.authentication_success` with your `secret_id`

---

### Step 7: Revoke Old Secret

After confirming the new secret works correctly, revoke the old one:

**Via Admin UI:**

1. In the Secrets list, find the old secret (status: **Active**)
2. Click **"Revoke"** button
3. Confirm revocation

**Via Admin API:**

```http
DELETE /api/admin/clients/{clientId}/secrets/{oldSecretId}
Authorization: Bearer {your-admin-token}
```

⚠️ **Safety check**: MrWhoOidc prevents you from revoking the last active secret (would lock you out).

---

## Emergency Rotation (Compromised Secret)

If a secret is compromised, follow this expedited process:

1. **Generate new secret immediately** (Step 1 above)
2. **Activate it immediately** (check "Activate immediately" during generation)
3. **Deploy updated config ASAP** (may cause brief downtime if not using rolling deployment)
4. **Revoke compromised secret** as soon as deployment completes
5. **Audit logs** to check if compromised secret was used maliciously

**Timeline goal**: Complete rotation within 1 hour of compromise detection.

---

## Troubleshooting

### "Invalid client credentials" after activation

**Cause**: Application still using old secret, or typo in configuration.

**Solution**:

1. Verify the secret value in your application config matches exactly (no extra spaces/newlines)
2. Check application logs for the error details
3. Ensure application reloaded the config (restart if necessary)
4. Temporarily re-activate the old secret if you need to rollback

### "Secret expired" error

**Cause**: Secret's expiry date has passed.

**Solution**:

1. Generate and activate a new secret immediately
2. Update and deploy your application
3. Remove expired secret from the Admin UI (cleanup)

### "Cannot revoke last active secret"

**Cause**: You're trying to revoke the only valid secret.

**Solution**:

1. Generate and activate a new secret first
2. Then revoke the old one

### Application using old secret after rotation

**Cause**: Some instances haven't reloaded the configuration.

**Solution**:

1. Check "Last Used" timestamps in Admin UI to identify which secret is being used
2. Perform a rolling restart of all application instances
3. Verify all instances are using the new secret before revoking the old one

---

## Best Practices

### Secret Management

- **Never commit secrets to source control** (Git, SVN, etc.)
- **Use secret stores**: Azure Key Vault, AWS Secrets Manager, HashiCorp Vault, or Kubernetes Secrets
- **Rotate regularly**: Set a recurring calendar reminder every 80-85 days (before 90-day expiry)
- **Document your secrets**: Use the description field to note environment, purpose, or rotation date
- **Limit access**: Only grant secret access to personnel who need it

### Deployment

- **Test first**: Always test rotation in dev/staging before production
- **Gradual rollout**: Use blue/green or canary deployments to limit blast radius
- **Monitoring**: Set up alerts for authentication failures before rotating
- **Backup plan**: Know how to quickly rollback your application config

### Expiry Management

- **Set expiry dates**: Use the default 90-day expiry unless you have a specific reason not to
- **Watch for warnings**: MrWhoOidc will emit warnings 7 days before expiry
- **Automate reminders**: Use the Admin API to build automated rotation reminders

### Security

- **Audit access**: Review who has access to your client secrets
- **Use principle of least privilege**: Don't share production secrets with dev environments
- **Rotate after incidents**: Always rotate after suspected compromise or personnel changes
- **Monitor usage**: Check "Last Used" timestamps; investigate secrets that haven't been used in >30 days

---

## Automation (Advanced)

For high-security environments, consider automating rotation:

### Using Admin API in CI/CD

Example GitHub Actions workflow:

```yaml
name: Rotate Client Secret

on:
  schedule:
    - cron: '0 2 * * 0'  # Weekly on Sunday at 2 AM UTC
  workflow_dispatch:      # Allow manual trigger

jobs:
  rotate:
    runs-on: ubuntu-latest
    steps:
      - name: Generate new secret
        id: generate
        run: |
          RESPONSE=$(curl -X POST \
            "https://auth.example.com/api/admin/clients/${{ secrets.CLIENT_ID }}/secrets" \
            -H "Authorization: Bearer ${{ secrets.ADMIN_TOKEN }}" \
            -H "Content-Type: application/json" \
            -d '{"description":"Auto-rotated secret","expiresInDays":90,"activateImmediately":false}')
          
          SECRET_ID=$(echo $RESPONSE | jq -r '.secretId')
          SECRET_VALUE=$(echo $RESPONSE | jq -r '.secretValue')
          
          echo "::add-mask::$SECRET_VALUE"
          echo "SECRET_ID=$SECRET_ID" >> $GITHUB_OUTPUT
          echo "SECRET_VALUE=$SECRET_VALUE" >> $GITHUB_OUTPUT

      - name: Update Azure Key Vault
        run: |
          az keyvault secret set \
            --vault-name "my-keyvault" \
            --name "oidc-client-secret" \
            --value "${{ steps.generate.outputs.SECRET_VALUE }}"

      - name: Trigger application deployment
        run: |
          # Trigger your deployment pipeline
          gh workflow run deploy.yml

      - name: Wait for deployment
        run: sleep 300  # Wait 5 minutes for deployment

      - name: Activate new secret
        run: |
          curl -X POST \
            "https://auth.example.com/api/admin/clients/${{ secrets.CLIENT_ID }}/secrets/${{ steps.generate.outputs.SECRET_ID }}/activate" \
            -H "Authorization: Bearer ${{ secrets.ADMIN_TOKEN }}"

      - name: Wait and verify
        run: sleep 3600  # Wait 1 hour for verification

      # Manual step: Verify monitoring, then trigger revoke workflow
```

---

## FAQ

**Q: How many secrets can I have active at once?**  
A: Maximum 3 active secrets per client. This allows for overlap during rotation without clutter.

**Q: Can I rotate without downtime?**  
A: Yes! Follow the overlap workflow (Steps 1-7) to rotate with zero downtime.

**Q: What happens if I lose a secret?**  
A: Generate a new one immediately. You cannot retrieve an existing secret (it's hashed in the database).

**Q: Can I extend the expiry of an existing secret?**  
A: No. Expiry dates are immutable. Generate a new secret and revoke the old one.

**Q: Do public clients (SPAs, mobile apps) need secret rotation?**  
A: No. Public clients don't use client secrets. They use PKCE instead.

**Q: What if all my secrets expire?**  
A: Your client will be unable to authenticate. Contact your MrWhoOidc administrator immediately to generate a new secret.

---

## Support

If you encounter issues during rotation:

1. Check the [Troubleshooting](#troubleshooting) section above
2. Review your application logs for detailed error messages
3. Contact your MrWhoOidc administrator
4. Consult the [Admin Guide](admin-guide.md) for more details on the Secrets management UI

---

## Related Documentation

- [Admin Guide](admin-guide.md) — Overview of the Admin UI
- [Client Secret Rotation Playbook](client-secret-rotation-playbook.md) — Operational playbook for admins
- [Security Best Practices](security/) — General security recommendations

---

**Document Version History:**

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2025-10-17 | Initial release with overlap rotation workflow |
