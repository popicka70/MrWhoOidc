# Well-Known Identity Provider Templates Plan

> **Partially implemented design; status clarified 2026-09-05.** [WellKnownProviderCatalog](../MrWhoOidc.Auth/IdentityProviders/WellKnownProviderCatalog.cs) and [template definitions](../MrWhoOidc.Auth/IdentityProviders/WellKnownProviderTemplate.cs) now exist. Statements below that no template support exists describe the original proposal. Unchecked UI, migration, and integration requirements still need individual verification; see [documentation status](documentation-status.md).

**Feature ID**: 014-wellknown-idp-templates  
**Date**: 2025-12-29  
**Status**: Draft / Research Complete  
**Author**: MrWho IdP Team

## Executive Summary

This plan details the implementation of pre-configured templates for well-known external identity providers (Microsoft Entra ID, Google, Facebook, Apple, GitHub, LinkedIn, etc.). The goal is to simplify IdP integration for administrators by providing:

1. **Provider-specific templates** with pre-filled OIDC configuration
2. **Built-in provider icons/logos** for consistent branding
3. **Provider-specific UI workflows** with tailored configuration options (e.g., Entra ID tenant selection)
4. **Default claim mappings** aligned with each provider's token structure
5. **Guided setup experience** with links to provider documentation

---

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Goals & Non-Goals](#goals--non-goals)
3. [Well-Known Provider Catalog](#well-known-provider-catalog)
4. [Data Model Changes](#data-model-changes)
5. [UI/UX Design](#uiux-design)
6. [Provider-Specific Configurations](#provider-specific-configurations)
7. [Implementation Phases](#implementation-phases)
8. [File Structure](#file-structure)
9. [Testing Strategy](#testing-strategy)
10. [Documentation Requirements](#documentation-requirements)
11. [Open Questions](#open-questions)
12. [References](#references)

---

## Problem Statement

Currently, MrWhoOidc supports adding any OIDC-compatible external identity provider through a generic form. While flexible, this approach has several pain points:

| Pain Point | Description |
|------------|-------------|
| **Manual Configuration** | Admins must manually look up discovery URLs, configure scopes, and understand provider-specific quirks |
| **No Provider Branding** | No built-in icons; admins must find and host logos themselves |
| **Missing Provider-Specific Features** | No support for Entra ID tenant IDs, Google hosted domain (`hd`), Facebook API versions |
| **Generic Claim Mappings** | Standard OIDC claims assumed; providers like Apple require special handling |
| **Error-Prone Setup** | Easy to misconfigure authority URLs, scopes, or response types |

---

## Goals & Non-Goals

### Goals (P1 - Must Have)

- [ ] Provide templates for 6+ major OIDC providers
- [ ] Auto-populate OIDC configuration from template selection
- [ ] Include embedded SVG icons for each provider (no external dependencies)
- [ ] Support Microsoft Entra ID tenant configuration (common, organizations, consumers, specific tenant)
- [ ] Pre-configured default claim mappings per provider
- [ ] Provider-specific help text and documentation links

### Goals (P2 - Should Have)

- [ ] Guided wizard-style setup flow
- [ ] Provider-specific validation rules
- [ ] "Test Connection" button with provider-specific checks
- [ ] Import from discovery URL for custom providers
- [ ] Provider-specific logout handling

### Non-Goals

- SAML provider templates (out of scope for this feature)
- Social login SDK integration (we use standard OIDC)
- Provider-specific MFA/step-up authentication flows
- Device authorization flow templates

---

## Well-Known Provider Catalog

### Tier 1: Full Support (Built-in Templates + Icons + Claim Mappings)

| Provider | Authority Pattern | Special Features | Default Scopes |
|----------|------------------|------------------|----------------|
| **Microsoft Entra ID** | `https://login.microsoftonline.com/{tenant}/v2.0` | Multi-tenant support (common/organizations/consumers/tenant-id), `domain_hint`, `login_hint` | `openid profile email` |
| **Google** | `https://accounts.google.com` | `hd` parameter for Google Workspace, prompt options | `openid profile email` |
| **Facebook** | `https://www.facebook.com` | API versioning, Limited Login support | `openid email public_profile` |
| **Apple** | `https://appleid.apple.com` | Private Email Relay, user info only on first auth, client secret JWT | `openid email name` |
| **GitHub** | `https://github.com` | OAuth2 + limited OIDC, organization restrictions | `openid user:email` |
| **LinkedIn** | `https://www.linkedin.com/oauth` | OpenID Connect certified | `openid profile email` |

### Tier 2: Template + Icon Only

| Provider | Authority Pattern | Notes |
|----------|------------------|-------|
| **Okta** | `https://{domain}.okta.com` | Custom domain support |
| **Auth0** | `https://{tenant}.auth0.com` | Tenant-based authority |
| **Keycloak** | `https://{host}/realms/{realm}` | Realm-based authority |
| **AWS Cognito** | `https://cognito-idp.{region}.amazonaws.com/{userPoolId}` | Region + pool ID |
| **Ping Identity** | Configurable | Enterprise focus |
| **OneLogin** | `https://{subdomain}.onelogin.com/oidc/2` | Subdomain-based |

### Tier 3: Generic OIDC (Current Behavior)

Any OIDC-compliant provider not in the above lists.

---

## Data Model Changes

### Option A: Embedded Provider Type (Recommended)

Add a `ProviderTemplate` enum and optional properties to `IdentityProvider`:

```csharp
// In AuthDbContext.cs or new file MrWhoOidc.Auth/IdentityProviders/WellKnownProvider.cs

/// <summary>
/// Well-known identity provider templates for simplified configuration.
/// </summary>
public enum WellKnownProviderTemplate
{
    Custom = 0,           // Generic OIDC (current behavior)
    MicrosoftEntraId = 1,
    Google = 2,
    Facebook = 3,
    Apple = 4,
    GitHub = 5,
    LinkedIn = 6,
    Okta = 7,
    Auth0 = 8,
    Keycloak = 9,
    AwsCognito = 10,
    PingIdentity = 11,
    OneLogin = 12
}

// Extension to IdentityProvider entity
public class IdentityProvider
{
    // ... existing properties ...
    
    /// <summary>
    /// Template used to create this provider. Null/Custom for manually configured providers.
    /// </summary>
    public WellKnownProviderTemplate? ProviderTemplate { get; set; }
    
    /// <summary>
    /// Provider-specific configuration (e.g., Entra tenant ID, Google hosted domain).
    /// Stored as JSON within ConfigJson or as separate field.
    /// </summary>
    [MaxLength(4000)]
    public string? ProviderSpecificConfig { get; set; }
}
```

### Option B: Metadata-Driven (Alternative)

Store provider templates in a separate table or JSON file:

```csharp
public class IdentityProviderTemplate
{
    public string Id { get; set; }           // e.g., "microsoft-entra-id"
    public string DisplayName { get; set; }  // e.g., "Microsoft Entra ID"
    public string Description { get; set; }
    public string AuthorityPattern { get; set; }
    public string IconSvg { get; set; }      // Embedded SVG
    public string[] DefaultScopes { get; set; }
    public string[] SupportedFeatures { get; set; }
    public Dictionary<string, string> DefaultClaimMappings { get; set; }
    public string DocumentationUrl { get; set; }
}
```

**Recommendation**: Option A is simpler and keeps the database schema minimal. Provider templates can be defined in code as a static catalog.

### Migration

```bash
# Add migration for ProviderTemplate column
dotnet ef migrations add AddProviderTemplateToIdentityProvider --project MrWhoOidc.Auth --startup-project MrWhoOidc.WebAuth --output-dir Persistence/Migrations
```

---

## UI/UX Design

### New Provider Selection Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│  Add Identity Provider                                              │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Choose a provider template:                                        │
│                                                                     │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐     │
│  │ [Microsoft      │  │ [Google         │  │ [Facebook       │     │
│  │  Entra ID logo] │  │  logo]          │  │  logo]          │     │
│  │                 │  │                 │  │                 │     │
│  │ Microsoft       │  │ Google          │  │ Facebook        │     │
│  │ Entra ID        │  │                 │  │                 │     │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘     │
│                                                                     │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐     │
│  │ [Apple          │  │ [GitHub         │  │ [LinkedIn       │     │
│  │  logo]          │  │  logo]          │  │  logo]          │     │
│  │                 │  │                 │  │                 │     │
│  │ Apple           │  │ GitHub          │  │ LinkedIn        │     │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘     │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │ [Custom OIDC icon]                                           │   │
│  │ Custom OIDC Provider                                         │   │
│  │ Configure any OIDC-compliant identity provider               │   │
│  └─────────────────────────────────────────────────────────────┘   │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Provider-Specific Configuration (Microsoft Entra ID Example)

```
┌─────────────────────────────────────────────────────────────────────┐
│  Add Microsoft Entra ID Provider                                    │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  [Microsoft Entra ID logo]  Microsoft Entra ID                      │
│                                                                     │
│  Basic Settings                                                     │
│  ─────────────────                                                  │
│  Name: [________________]  (required)                               │
│  Display Name: [Microsoft Login_______________]                     │
│                                                                     │
│  Tenant Configuration                                               │
│  ────────────────────                                               │
│  ○ Common (personal + work/school accounts)                         │
│  ○ Organizations only (work/school accounts)                        │
│  ○ Consumers only (personal Microsoft accounts)                     │
│  ● Specific tenant                                                  │
│     Tenant ID or Domain: [contoso.onmicrosoft.com____]              │
│                                                                     │
│  ℹ️ Authority URL: https://login.microsoftonline.com/{tenant}/v2.0  │
│                                                                     │
│  Application Registration                                           │
│  ────────────────────────                                           │
│  Client ID: [________________________________]  (required)          │
│  Client Secret: [****************************]                      │
│                                                                     │
│  📖 How to register an app in Entra ID                              │
│  📖 Configure redirect URIs                                         │
│                                                                     │
│  [▼ Advanced Options]                                               │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │ Domain Hint: [____________________]                            │  │
│  │ Login Hint (optional): [__________]                            │  │
│  │ Prompt: [select_account ▼]                                     │  │
│  │ □ Use PKCE (recommended)  ☑                                    │  │
│  │ □ Use PAR                 ☐                                    │  │
│  └───────────────────────────────────────────────────────────────┘  │
│                                                                     │
│  [Test Connection]  [Save Provider]  [Cancel]                       │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### File Structure for Pages

```
MrWhoOidc.WebAuth/Pages/Admin/Providers/
├── Index.cshtml              # List (existing)
├── Add.cshtml                # Becomes template selector
├── Add.cshtml.cs             
├── Templates/
│   ├── _ProviderSelector.cshtml       # Template grid partial
│   ├── _MicrosoftEntraIdForm.cshtml   # Entra-specific form
│   ├── _GoogleForm.cshtml             # Google-specific form
│   ├── _AppleForm.cshtml              # Apple-specific form
│   ├── _FacebookForm.cshtml           # Facebook-specific form
│   ├── _GitHubForm.cshtml             # GitHub-specific form
│   ├── _LinkedInForm.cshtml           # LinkedIn-specific form
│   └── _CustomOidcForm.cshtml         # Generic form (existing)
├── Edit.cshtml               # Edit (existing, enhanced)
├── Delete.cshtml             # Delete (existing)
└── Details.cshtml            # Details (existing)
```

---

## Provider-Specific Configurations

### 1. Microsoft Entra ID

| Configuration | Value | Notes |
|---------------|-------|-------|
| Authority | `https://login.microsoftonline.com/{tenant}/v2.0` | Tenant: common, organizations, consumers, or tenant ID |
| Discovery | `{authority}/.well-known/openid-configuration` | Standard |
| Default Scopes | `openid profile email` | |
| Response Type | `code` | |
| PKCE | Required | Always enable |
| Extra Params | `domain_hint`, `login_hint`, `prompt` | Optional |

**Claim Mappings:**

| External Claim | Local Claim |
|----------------|-------------|
| `sub` | `sub` |
| `preferred_username` | `email` |
| `name` | `name` |
| `given_name` | `given_name` |
| `family_name` | `family_name` |
| `email` | `email` |
| `tid` | `tenant_id` |
| `oid` | `object_id` |

**Provider-Specific Config Schema:**

```json
{
  "tenantType": "specific",    // "common" | "organizations" | "consumers" | "specific"
  "tenantId": "contoso.onmicrosoft.com",
  "domainHint": "",
  "loginHint": "",
  "prompt": "select_account"   // "none" | "login" | "consent" | "select_account"
}
```

### 2. Google

| Configuration | Value | Notes |
|---------------|-------|-------|
| Authority | `https://accounts.google.com` | Fixed |
| Discovery | `https://accounts.google.com/.well-known/openid-configuration` | Standard |
| Default Scopes | `openid profile email` | |
| Response Type | `code` | |
| PKCE | Recommended | |
| Extra Params | `hd` (hosted domain), `login_hint`, `prompt` | |

**Claim Mappings:**

| External Claim | Local Claim |
|----------------|-------------|
| `sub` | `sub` |
| `email` | `email` |
| `email_verified` | `email_verified` |
| `name` | `name` |
| `given_name` | `given_name` |
| `family_name` | `family_name` |
| `picture` | `picture` |
| `locale` | `locale` |
| `hd` | `hosted_domain` |

**Provider-Specific Config Schema:**

```json
{
  "hostedDomain": "",          // Google Workspace domain filter
  "loginHint": "",
  "prompt": "select_account",  // "none" | "consent" | "select_account"
  "accessType": "online"       // "online" | "offline" (for refresh tokens)
}
```

### 3. Facebook

| Configuration | Value | Notes |
|---------------|-------|-------|
| Authority | `https://www.facebook.com` | |
| Authorization Endpoint | `https://www.facebook.com/v19.0/dialog/oauth` | Versioned |
| Token Endpoint | `https://graph.facebook.com/v19.0/oauth/access_token` | Versioned |
| Discovery | Manual configuration (no standard discovery) | |
| Default Scopes | `openid email public_profile` | |
| Response Type | `code` | |
| PKCE | Supported | |

**Claim Mappings:**

| External Claim | Local Claim |
|----------------|-------------|
| `sub` | `sub` |
| `email` | `email` |
| `name` | `name` |
| `first_name` | `given_name` |
| `last_name` | `family_name` |
| `picture.data.url` | `picture` |

**Provider-Specific Config Schema:**

```json
{
  "apiVersion": "v19.0",
  "enableReauthorization": false
}
```

### 4. Apple

| Configuration | Value | Notes |
|---------------|-------|-------|
| Authority | `https://appleid.apple.com` | |
| Discovery | `https://appleid.apple.com/.well-known/openid-configuration` | |
| Default Scopes | `openid email name` | |
| Response Type | `code` | |
| Response Mode | `form_post` | Required |
| Client Authentication | `private_key_jwt` | Uses ES256 key |

**Claim Mappings:**

| External Claim | Local Claim |
|----------------|-------------|
| `sub` | `sub` |
| `email` | `email` |
| `email_verified` | `email_verified` |
| `is_private_email` | `is_private_email` |
| `real_user_status` | `real_user_status` |

**Special Considerations:**

1. **User info only on first auth**: Apple provides name only on first authorization. Store it immediately.
2. **Private Email Relay**: Apple may provide a relay email (`xxxxx@privaterelay.appleid.com`).
3. **Client Secret Generation**: Apple requires a JWT client secret generated from a private key with short expiry (6 months max).

**Provider-Specific Config Schema:**

```json
{
  "teamId": "XXXXXXXXXX",
  "keyId": "YYYYYYYYYY",
  "privateKeyPem": "-----BEGIN PRIVATE KEY-----\n...",
  "clientSecretExpiryDays": 180
}
```

### 5. GitHub

| Configuration | Value | Notes |
|---------------|-------|-------|
| Authority | `https://github.com` | |
| Authorization Endpoint | `https://github.com/login/oauth/authorize` | |
| Token Endpoint | `https://github.com/login/oauth/access_token` | |
| Discovery | Manual configuration (limited OIDC) | |
| Default Scopes | `read:user user:email` | OAuth scopes |
| Response Type | `code` | |

**Claim Mappings:**

| External Claim | Local Claim |
|----------------|-------------|
| `id` | `sub` |
| `login` | `preferred_username` |
| `email` | `email` |
| `name` | `name` |
| `avatar_url` | `picture` |

**Special Considerations:**

1. **Not fully OIDC-compliant**: GitHub uses OAuth2 with user info endpoint, not ID tokens.
2. **Organization restrictions**: Can limit login to members of specific orgs.
3. **Primary email**: May need to call `/user/emails` endpoint for verified email.

**Provider-Specific Config Schema:**

```json
{
  "allowedOrganizations": [],
  "allowedTeams": [],
  "allowPrivateEmails": false
}
```

### 6. LinkedIn

| Configuration | Value | Notes |
|---------------|-------|-------|
| Authority | `https://www.linkedin.com/oauth` | |
| Authorization Endpoint | `https://www.linkedin.com/oauth/v2/authorization` | |
| Token Endpoint | `https://www.linkedin.com/oauth/v2/accessToken` | |
| Discovery | `https://www.linkedin.com/oauth/.well-known/openid-configuration` | |
| Default Scopes | `openid profile email` | |
| Response Type | `code` | |

**Claim Mappings:**

| External Claim | Local Claim |
|----------------|-------------|
| `sub` | `sub` |
| `email` | `email` |
| `email_verified` | `email_verified` |
| `name` | `name` |
| `given_name` | `given_name` |
| `family_name` | `family_name` |
| `picture` | `picture` |
| `locale` | `locale` |

---

## Implementation Phases

### Phase 1: Foundation (Week 1-2)

**Tasks:**

1. [ ] Create `WellKnownProviderTemplate` enum
2. [ ] Create `WellKnownProviderCatalog` static class with template definitions
3. [ ] Add `ProviderTemplate` column to `IdentityProvider` entity
4. [ ] Create database migration
5. [ ] Embed SVG icons as resources (or inline in catalog)
6. [ ] Create `_ProviderSelector.cshtml` partial view

**Deliverables:**

- Data model updated
- Provider catalog with metadata
- Icon assets embedded

### Phase 2: UI Implementation (Week 3-4)

**Tasks:**

1. [ ] Modify `Add.cshtml` to show provider selector
2. [ ] Create provider-specific form partials
3. [ ] Implement form switching based on template selection
4. [ ] Add JavaScript for dynamic form population
5. [ ] Update `AddModel` code-behind for template handling
6. [ ] Add provider-specific help text and links

**Deliverables:**

- Template selection UI
- Provider-specific forms for Tier 1 providers
- Auto-population of OIDC configuration

### Phase 3: Provider-Specific Features (Week 5-6)

**Tasks:**

1. [ ] Implement Entra ID tenant type selection
2. [ ] Implement Google hosted domain (`hd`) support
3. [ ] Implement Apple client secret JWT generation
4. [ ] Implement GitHub OAuth-to-claims mapping
5. [ ] Add default claim mappings per template
6. [ ] Create "Test Connection" feature

**Deliverables:**

- Provider-specific configuration working
- Claim mappings auto-configured
- Connection testing

### Phase 4: Polish & Documentation (Week 7-8)

**Tasks:**

1. [ ] Add validation rules per provider
2. [ ] Create admin guide for each provider
3. [ ] Add unit tests for catalog and form handling
4. [ ] Add integration tests for provider flow
5. [ ] Update copilot-instructions.md
6. [ ] Create video/gif demos

**Deliverables:**

- Complete documentation
- Test coverage
- Ready for release

---

## File Structure

```
MrWhoOidc.Auth/
├── IdentityProviders/
│   ├── WellKnownProviderTemplate.cs      # Enum definition
│   ├── WellKnownProviderCatalog.cs       # Static catalog with metadata
│   ├── ProviderTemplateDefinition.cs     # Template metadata class
│   ├── ProviderSpecificConfig/
│   │   ├── EntraIdConfig.cs
│   │   ├── GoogleConfig.cs
│   │   ├── AppleConfig.cs
│   │   ├── FacebookConfig.cs
│   │   ├── GitHubConfig.cs
│   │   └── LinkedInConfig.cs
│   └── ClaimMappings/
│       ├── DefaultClaimMappingProvider.cs
│       └── ProviderClaimMappings.cs
├── Persistence/
│   ├── AuthDbContext.cs                   # Updated IdentityProvider entity
│   └── Migrations/
│       └── XXXXXX_AddProviderTemplate.cs  # New migration

MrWhoOidc.WebAuth/
├── Pages/Admin/Providers/
│   ├── Add.cshtml                         # Updated with template selector
│   ├── Add.cshtml.cs                      # Updated handler
│   ├── Templates/
│   │   ├── _ProviderSelector.cshtml
│   │   ├── _MicrosoftEntraIdForm.cshtml
│   │   ├── _GoogleForm.cshtml
│   │   ├── _AppleForm.cshtml
│   │   ├── _FacebookForm.cshtml
│   │   ├── _GitHubForm.cshtml
│   │   ├── _LinkedInForm.cshtml
│   │   └── _CustomOidcForm.cshtml
│   └── Shared/
│       └── _ProviderIcons.cshtml          # SVG icons partial
├── wwwroot/
│   └── images/providers/                   # Alternative: PNG fallback icons
│       ├── microsoft.svg
│       ├── google.svg
│       ├── apple.svg
│       ├── facebook.svg
│       ├── github.svg
│       └── linkedin.svg

MrWhoOidc.UnitTests/
└── IdentityProviders/
    ├── WellKnownProviderCatalogTests.cs
    ├── ProviderSpecificConfigTests.cs
    └── TemplateClaimMappingTests.cs
```

---

## Testing Strategy

### Unit Tests

| Test Class | Coverage |
|------------|----------|
| `WellKnownProviderCatalogTests` | Catalog returns correct metadata for each provider |
| `ProviderSpecificConfigTests` | JSON serialization/deserialization of provider configs |
| `EntraIdAuthorityBuilderTests` | Correct authority URL generation for tenant types |
| `AppleClientSecretGeneratorTests` | JWT client secret generation with valid signature |
| `TemplateClaimMappingTests` | Default claim mappings applied correctly |

### Integration Tests

| Test | Description |
|------|-------------|
| `AddProviderWithTemplateTest` | Create provider using template, verify config populated |
| `EntraIdTenantFlowTest` | E2E test with specific tenant (mocked Entra response) |
| `GoogleHostedDomainTest` | E2E test with hd parameter verification |
| `TemplateMigrationTest` | Existing providers remain functional after migration |

### Manual Testing Checklist

- [ ] Add Microsoft Entra ID provider with "common" tenant
- [ ] Add Microsoft Entra ID provider with specific tenant ID
- [ ] Add Google provider with hosted domain restriction
- [ ] Add Apple provider with private key configuration
- [ ] Add custom OIDC provider (verify backward compatibility)
- [ ] Edit template-created provider and verify config preserved
- [ ] Delete template-created provider
- [ ] Verify icons display correctly in provider list

---

## Documentation Requirements

### Admin Guide Updates

| Document | Updates Needed |
|----------|----------------|
| `admin-guide.md` | Section on adding well-known providers |
| `developer-guide.md` | API changes for template field |
| New: `provider-setup-guides/` | Per-provider setup instructions |

### Provider-Specific Setup Guides

Create `docs/provider-setup-guides/` directory with:

1. `microsoft-entra-id-setup.md` - App registration walkthrough
2. `google-setup.md` - Google Cloud Console configuration
3. `apple-setup.md` - Apple Developer Portal setup + key generation
4. `facebook-setup.md` - Meta Developer Console configuration
5. `github-setup.md` - GitHub OAuth App creation
6. `linkedin-setup.md` - LinkedIn Developer Portal configuration

Each guide should include:

- Screenshots of provider console
- Required permissions/scopes
- Redirect URI format
- Common troubleshooting tips

---

## Open Questions

| Question | Options | Decision |
|----------|---------|----------|
| **Icon storage approach** | A) Embedded SVG in code, B) Static files in wwwroot, C) CDN links | TBD |
| **Apple private key storage** | A) In ConfigJson encrypted, B) Separate secure store, C) Reference to key vault | TBD |
| **GitHub OAuth handling** | A) Treat as OAuth2 (not OIDC), B) Custom handler, C) Reject as not OIDC | TBD |
| **Facebook API versioning** | A) Hardcode latest, B) Admin configurable, C) Auto-detect | TBD |
| **Backward compatibility** | A) Auto-detect existing providers as templates, B) Leave as Custom | TBD |

---

## References

### Official Provider Documentation

| Provider | Documentation URL |
|----------|-------------------|
| Microsoft Entra ID | https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc |
| Google | https://developers.google.com/identity/openid-connect/openid-connect |
| Facebook | https://developers.facebook.com/docs/facebook-login/guides/advanced/oidc-token |
| Apple | https://developer.apple.com/documentation/sign_in_with_apple |
| GitHub | https://docs.github.com/en/apps/oauth-apps/building-oauth-apps |
| LinkedIn | https://learn.microsoft.com/en-us/linkedin/shared/authentication/authentication |

### Discovery Documents

```
Microsoft (common): https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration
Google:             https://accounts.google.com/.well-known/openid-configuration
Apple:              https://appleid.apple.com/.well-known/openid-configuration
LinkedIn:           https://www.linkedin.com/oauth/.well-known/openid-configuration
```

### Related Specs in This Project

- `specs/013-external-idp-registration/` - External IdP registration flow
- `specs/012-oidc-export-import/` - Configuration import/export

---

## Appendix A: Provider Icon SVGs

Icons should be sourced from official brand resources and comply with each provider's brand guidelines:

| Provider | Brand Guidelines |
|----------|------------------|
| Microsoft | https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-branding-in-apps |
| Google | https://developers.google.com/identity/branding-guidelines |
| Apple | https://developer.apple.com/design/human-interface-guidelines/sign-in-with-apple |
| Facebook | https://developers.facebook.com/docs/facebook-login/userexperience |
| GitHub | https://github.com/logos |
| LinkedIn | https://brand.linkedin.com |

---

## Appendix B: Default Claim Mapping Definitions

```csharp
public static class DefaultClaimMappings
{
    public static readonly Dictionary<WellKnownProviderTemplate, List<ClaimMapping>> Mappings = new()
    {
        [WellKnownProviderTemplate.MicrosoftEntraId] = new()
        {
            new("sub", "sub"),
            new("preferred_username", "email"),
            new("name", "name"),
            new("given_name", "given_name"),
            new("family_name", "family_name"),
            new("email", "email"),
            new("tid", "tenant_id"),
            new("oid", "object_id")
        },
        [WellKnownProviderTemplate.Google] = new()
        {
            new("sub", "sub"),
            new("email", "email"),
            new("email_verified", "email_verified"),
            new("name", "name"),
            new("picture", "picture"),
            new("hd", "hosted_domain")
        },
        // ... other providers
    };
}
```

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 0.1 | 2025-12-29 | Copilot | Initial draft |
