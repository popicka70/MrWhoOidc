# Quickstart: External IdP Registration

**Feature**: 013-external-idp-registration  
**Date**: 2025-12-25

## Overview

This guide explains how to enable external identity provider (IdP) registration for your MrWhoOidc deployment. Once configured, users can register new accounts by authenticating with external providers like Google, Microsoft, or enterprise SSO systems. Current registration can also target a tenant through invitation links or verified auto-join domain claims.

---

## Prerequisites

- MrWhoOidc deployment running with at least one external IdP configured
- Admin access to configure identity providers
- External IdP (e.g., Google, Microsoft Entra ID) with client credentials

---

## Step 1: Configure External IdP

If you haven't already configured an external IdP, follow these steps:

1. Navigate to **Admin > Identity Providers**
2. Click **Add Provider**
3. Enter provider details:
   - **Name**: Unique identifier (e.g., `google`)
   - **Display Name**: User-friendly name (e.g., `Google`)
   - **Type**: OIDC
   - **Authority**: Provider's OIDC authority URL
   - **Client ID**: Your application's client ID from the IdP
   - **Client Secret**: Your application's client secret
4. Click **Save**

---

## Step 2: Enable Registration for the IdP

1. Navigate to **Admin > Identity Providers**
2. Click **Edit** on the provider you want to enable for registration
3. Find the **Allow Registration** checkbox
4. Check the box to enable registration via this provider
5. Click **Save**

**Note**: Only IdPs with both **Enabled** and **Allow Registration** checked will appear on the registration page.

---

## Step 3: Verify Registration Page

1. Sign out or open an incognito window
2. Navigate to the registration page: `/Registrations`
3. You should see:
   - The traditional manual registration form
   - Buttons for each registration-enabled IdP

---

## User Registration Flow

### Via External IdP

1. User visits `/Registrations`
2. User clicks the external IdP button (e.g., "Sign up with Google")
3. User is redirected to the external IdP to authenticate
4. After successful authentication, user is redirected back
5. A new account is created using information from the IdP:
   - Email address
   - First name (if provided)
   - Last name (if provided)
6. User sees success message and can proceed to sign in

### Via Manual Form

The traditional registration form remains available for users who prefer to create accounts manually or don't have access to configured IdPs.

Manual registration is auto-approved when the user is creating a tenant, registering from an invitation, matching a verified `AutoJoin` tenant domain claim, or using a client policy that allows auto-approval. Otherwise the registration remains pending for tenant admin review.

### Via Invitation

Tenant admins create invitation links from **Admin -> Invitations**. The invite fixes the target tenant and email address. New invited users are auto-approved into the invitation tenant after registration; existing users can sign in with the invited email to accept.

### Via Domain Claim

Tenant admins reserve email domains from **Admin -> Domain claims**. A verified `AutoJoin` domain claim allows users with matching emails to discover the tenant from `/DiscoverTenant` and auto-register into that tenant. A non-revoked domain can be claimed by only one tenant across the platform.

---

## Configuration Options

### IdP Sort Order

Control the display order of IdP buttons using the **Sort Order** field in the IdP configuration. Lower numbers appear first.

### Disable Registration for Specific IdPs

Not all IdPs should necessarily be available for registration. For example:

- **Enable for registration**: Consumer IdPs (Google, Microsoft)
- **Disable for registration**: Enterprise IdPs used only for existing employee login

Use the **Allow Registration** toggle per-IdP to control this.

---

## Troubleshooting

### IdP Button Not Appearing

Check that the IdP has:

- [x] **Enabled** = true
- [x] **Allow Registration** = true
- [x] Belongs to the public registration tenant, which is the default tenant in the current deployment model

### Registration Fails with "User Already Exists"

This occurs when the email from the external IdP matches an existing account. The user should:

1. Sign in with their existing credentials
2. Optionally link the external IdP to their existing account (if supported)

### Missing Required Information

Some IdPs may not provide all expected claims. If registration fails due to missing email:

1. Check IdP configuration to ensure `email` scope is requested
2. Ensure the user's IdP account has an email address
3. User can fall back to manual registration

---

## Security Considerations

- Only IdPs explicitly enabled for registration will appear on the registration page
- Email addresses from external IdPs are validated before account creation
- Duplicate email addresses are detected and rejected
- Platform external login does not auto-enroll users into tenants
- Tenant external login can use domain auto-enrollment only when mapped claims include `email_verified=true`
- Tenant domain claims are platform-unique while active/non-revoked
- See [User Registration and Tenant Enrollment](../../../docs/user-registration-and-enrollment.md) for current invitation and domain claim behavior

---

## Related Documentation

- [Provider Management Guide](../../../docs/admin-guide.md)
- [External OIDC Authentication](../../../docs/developer-guide.md)
- [Multi-Tenancy](../../../docs/admin-guide.md#multi-tenancy)
