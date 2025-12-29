using System.Collections.Frozen;
using System.Collections.Immutable;

namespace MrWhoOidc.Auth.IdentityProviders;

/// <summary>
/// Static catalog of well-known identity provider templates.
/// Provides pre-configured settings for popular OIDC identity providers.
/// </summary>
public static class WellKnownProviderCatalog
{
    /// <summary>
    /// Gets all available provider templates.
    /// </summary>
    public static FrozenDictionary<WellKnownProviderTemplate, ProviderTemplateDefinition> Templates { get; } =
        CreateTemplates().ToFrozenDictionary(t => t.Template);

    /// <summary>
    /// Gets all available provider templates as a collection.
    /// </summary>
    public static IEnumerable<ProviderTemplateDefinition> GetAllTemplates() =>
        Templates.Values.Where(t => t.Template != WellKnownProviderTemplate.Custom);

    /// <summary>
    /// Gets Tier 1 providers (full support with icons and claim mappings).
    /// </summary>
    public static IEnumerable<ProviderTemplateDefinition> Tier1Providers =>
        Templates.Values.Where(t => t.Tier == 1 && t.Template != WellKnownProviderTemplate.Custom);

    /// <summary>
    /// Gets Tier 2 providers (template + icon only).
    /// </summary>
    public static IEnumerable<ProviderTemplateDefinition> Tier2Providers =>
        Templates.Values.Where(t => t.Tier == 2);

    /// <summary>
    /// Gets the template definition for a specific provider.
    /// </summary>
    public static ProviderTemplateDefinition? GetTemplate(WellKnownProviderTemplate template) =>
        Templates.GetValueOrDefault(template);

    /// <summary>
    /// Builds the authority URL from a template and placeholder values.
    /// </summary>
    public static string BuildAuthorityUrl(WellKnownProviderTemplate template, Dictionary<string, string>? placeholderValues)
    {
        var def = GetTemplate(template);
        if (def is null) return string.Empty;

        var authority = def.AuthorityPattern;
        if (placeholderValues is not null)
        {
            foreach (var (key, value) in placeholderValues)
            {
                authority = authority.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);
            }
        }
        return authority;
    }

    private static IEnumerable<ProviderTemplateDefinition> CreateTemplates()
    {
        // Custom (Generic OIDC)
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.Custom,
            DisplayName = "Custom OIDC",
            Description = "Configure any OIDC-compliant identity provider manually.",
            AuthorityPattern = "",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.CustomOidc,
            BrandColor = "#6c757d",
            Tier = 3,
            HelpText = "Enter the full authority URL and other OIDC configuration details for your provider."
        };

        // Microsoft Entra ID
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.MicrosoftEntraId,
            DisplayName = "Microsoft Entra ID",
            Description = "Sign in with Microsoft work, school, or personal accounts.",
            AuthorityPattern = "https://login.microsoftonline.com/{tenant}/v2.0",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.Microsoft,
            BrandColor = "#0078d4",
            DocumentationUrl = "https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc",
            ConsoleUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
            Tier = 1,
            HelpText = "Register your application in the Microsoft Entra admin center and configure the redirect URI.",
            AuthorityPlaceholders =
            [
                new AuthorityPlaceholder
                {
                    Name = "tenant",
                    Label = "Tenant Configuration",
                    HelpText = "Choose the type of accounts that can sign in.",
                    DefaultValue = "common",
                    Options =
                    [
                        new PlaceholderOption { Value = "common", Label = "Common", Description = "Personal Microsoft accounts and work/school accounts" },
                        new PlaceholderOption { Value = "organizations", Label = "Organizations", Description = "Work/school accounts only" },
                        new PlaceholderOption { Value = "consumers", Label = "Consumers", Description = "Personal Microsoft accounts only" },
                        new PlaceholderOption { Value = "", Label = "Specific Tenant", Description = "Enter your tenant ID or domain" }
                    ]
                }
            ],
            ConfigFields =
            [
                new ProviderConfigField
                {
                    Name = "tenantId",
                    Label = "Tenant ID or Domain",
                    FieldType = "text",
                    HelpText = "Enter your tenant ID (GUID) or domain (e.g., contoso.onmicrosoft.com). Only required for 'Specific Tenant'.",
                    Placeholder = "contoso.onmicrosoft.com or xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                },
                new ProviderConfigField
                {
                    Name = "domainHint",
                    Label = "Domain Hint",
                    FieldType = "text",
                    HelpText = "Skip domain selection by specifying the domain.",
                    Placeholder = "contoso.com"
                },
                new ProviderConfigField
                {
                    Name = "prompt",
                    Label = "Prompt Behavior",
                    FieldType = "select",
                    DefaultValue = "select_account",
                    Options =
                    [
                        new PlaceholderOption { Value = "", Label = "Default", Description = "Use provider default behavior" },
                        new PlaceholderOption { Value = "login", Label = "Login", Description = "Force re-authentication" },
                        new PlaceholderOption { Value = "consent", Label = "Consent", Description = "Show consent prompt" },
                        new PlaceholderOption { Value = "select_account", Label = "Select Account", Description = "Show account picker" },
                        new PlaceholderOption { Value = "none", Label = "None", Description = "No UI prompt (SSO only)" }
                    ]
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "preferred_username", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" },
                new DefaultClaimMapping { ExternalClaim = "given_name", LocalClaim = "given_name" },
                new DefaultClaimMapping { ExternalClaim = "family_name", LocalClaim = "family_name" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "tid", LocalClaim = "tenant_id" },
                new DefaultClaimMapping { ExternalClaim = "oid", LocalClaim = "object_id" }
            ]
        };

        // Google
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.Google,
            DisplayName = "Google",
            Description = "Sign in with Google accounts and Google Workspace.",
            AuthorityPattern = "https://accounts.google.com",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.Google,
            BrandColor = "#4285f4",
            DocumentationUrl = "https://developers.google.com/identity/openid-connect/openid-connect",
            ConsoleUrl = "https://console.cloud.google.com/apis/credentials",
            Tier = 1,
            HelpText = "Create OAuth 2.0 credentials in the Google Cloud Console and configure the authorized redirect URI.",
            ConfigFields =
            [
                new ProviderConfigField
                {
                    Name = "hostedDomain",
                    Label = "Hosted Domain (hd)",
                    FieldType = "text",
                    HelpText = "Restrict sign-in to a specific Google Workspace domain. Leave empty to allow all Google accounts.",
                    Placeholder = "example.com"
                },
                new ProviderConfigField
                {
                    Name = "prompt",
                    Label = "Prompt Behavior",
                    FieldType = "select",
                    DefaultValue = "select_account",
                    Options =
                    [
                        new PlaceholderOption { Value = "", Label = "Default", Description = "Use provider default behavior" },
                        new PlaceholderOption { Value = "none", Label = "None", Description = "No UI prompt" },
                        new PlaceholderOption { Value = "consent", Label = "Consent", Description = "Always show consent" },
                        new PlaceholderOption { Value = "select_account", Label = "Select Account", Description = "Show account picker" }
                    ]
                },
                new ProviderConfigField
                {
                    Name = "accessType",
                    Label = "Access Type",
                    FieldType = "select",
                    DefaultValue = "online",
                    Options =
                    [
                        new PlaceholderOption { Value = "online", Label = "Online", Description = "Standard access (no refresh token)" },
                        new PlaceholderOption { Value = "offline", Label = "Offline", Description = "Request refresh token" }
                    ]
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "email_verified", LocalClaim = "email_verified" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" },
                new DefaultClaimMapping { ExternalClaim = "given_name", LocalClaim = "given_name" },
                new DefaultClaimMapping { ExternalClaim = "family_name", LocalClaim = "family_name" },
                new DefaultClaimMapping { ExternalClaim = "picture", LocalClaim = "picture" },
                new DefaultClaimMapping { ExternalClaim = "locale", LocalClaim = "locale" },
                new DefaultClaimMapping { ExternalClaim = "hd", LocalClaim = "hosted_domain" }
            ]
        };

        // Facebook
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.Facebook,
            DisplayName = "Facebook",
            Description = "Sign in with Facebook accounts.",
            AuthorityPattern = "https://www.facebook.com",
            DiscoveryUrlPattern = null, // Facebook requires manual endpoint configuration
            DefaultScopes = ["openid", "email", "public_profile"],
            IconSvg = ProviderIcons.Facebook,
            BrandColor = "#1877f2",
            DocumentationUrl = "https://developers.facebook.com/docs/facebook-login/guides/advanced/oidc-token",
            ConsoleUrl = "https://developers.facebook.com/apps/",
            Tier = 1,
            HelpText = "Create a Facebook app in the Meta Developer Console and configure the OAuth redirect URI.",
            ConfigFields =
            [
                new ProviderConfigField
                {
                    Name = "apiVersion",
                    Label = "API Version",
                    FieldType = "text",
                    DefaultValue = "v19.0",
                    HelpText = "Facebook Graph API version to use.",
                    Placeholder = "v19.0"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" },
                new DefaultClaimMapping { ExternalClaim = "first_name", LocalClaim = "given_name" },
                new DefaultClaimMapping { ExternalClaim = "last_name", LocalClaim = "family_name" },
                new DefaultClaimMapping { ExternalClaim = "picture.data.url", LocalClaim = "picture" }
            ],
            SupportsBackChannelLogout = false
        };

        // Apple
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.Apple,
            DisplayName = "Apple",
            Description = "Sign in with Apple ID.",
            AuthorityPattern = "https://appleid.apple.com",
            DefaultScopes = ["openid", "email", "name"],
            IconSvg = ProviderIcons.Apple,
            BrandColor = "#000000",
            DocumentationUrl = "https://developer.apple.com/documentation/sign_in_with_apple",
            ConsoleUrl = "https://developer.apple.com/account/resources/identifiers/list/serviceId",
            Tier = 1,
            RequiresSpecialClientAuth = true,
            HelpText = "Configure Sign in with Apple in the Apple Developer Portal. Note: User's name is only provided on the first sign-in.",
            ConfigFields =
            [
                new ProviderConfigField
                {
                    Name = "teamId",
                    Label = "Team ID",
                    FieldType = "text",
                    Required = true,
                    HelpText = "Your Apple Developer Team ID (10 characters).",
                    Placeholder = "XXXXXXXXXX"
                },
                new ProviderConfigField
                {
                    Name = "keyId",
                    Label = "Key ID",
                    FieldType = "text",
                    Required = true,
                    HelpText = "The ID of your Sign in with Apple private key.",
                    Placeholder = "YYYYYYYYYY"
                },
                new ProviderConfigField
                {
                    Name = "privateKey",
                    Label = "Private Key (PEM)",
                    FieldType = "textarea",
                    Required = true,
                    HelpText = "The ES256 private key in PEM format. This will be stored securely.",
                    Placeholder = "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "email_verified", LocalClaim = "email_verified" },
                new DefaultClaimMapping { ExternalClaim = "is_private_email", LocalClaim = "is_private_email" },
                new DefaultClaimMapping { ExternalClaim = "real_user_status", LocalClaim = "real_user_status" }
            ],
            SupportsBackChannelLogout = false
        };

        // GitHub
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.GitHub,
            DisplayName = "GitHub",
            Description = "Sign in with GitHub accounts.",
            AuthorityPattern = "https://github.com",
            DiscoveryUrlPattern = null, // GitHub is OAuth2, not full OIDC
            DefaultScopes = ["read:user", "user:email"],
            IconSvg = ProviderIcons.GitHub,
            BrandColor = "#24292e",
            DocumentationUrl = "https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps",
            ConsoleUrl = "https://github.com/settings/developers",
            Tier = 1,
            HelpText = "Create an OAuth App in GitHub Developer Settings. Note: GitHub uses OAuth 2.0 with limited OIDC support.",
            ConfigFields =
            [
                new ProviderConfigField
                {
                    Name = "allowedOrganizations",
                    Label = "Allowed Organizations",
                    FieldType = "text",
                    HelpText = "Comma-separated list of GitHub organization names. Leave empty to allow all users.",
                    Placeholder = "my-org, another-org"
                },
                new ProviderConfigField
                {
                    Name = "allowPrivateEmails",
                    Label = "Allow Private Emails",
                    FieldType = "checkbox",
                    DefaultValue = "true",
                    HelpText = "Accept private email addresses (noreply@github.com)."
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "id", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "login", LocalClaim = "preferred_username" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" },
                new DefaultClaimMapping { ExternalClaim = "avatar_url", LocalClaim = "picture" }
            ],
            SupportsBackChannelLogout = false
        };

        // LinkedIn
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.LinkedIn,
            DisplayName = "LinkedIn",
            Description = "Sign in with LinkedIn accounts.",
            AuthorityPattern = "https://www.linkedin.com/oauth",
            DiscoveryUrlPattern = "https://www.linkedin.com/oauth/.well-known/openid-configuration",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.LinkedIn,
            BrandColor = "#0a66c2",
            DocumentationUrl = "https://learn.microsoft.com/en-us/linkedin/shared/authentication/authentication",
            ConsoleUrl = "https://www.linkedin.com/developers/apps",
            Tier = 1,
            HelpText = "Create an app in the LinkedIn Developer Portal and add the Sign In with LinkedIn product.",
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "email_verified", LocalClaim = "email_verified" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" },
                new DefaultClaimMapping { ExternalClaim = "given_name", LocalClaim = "given_name" },
                new DefaultClaimMapping { ExternalClaim = "family_name", LocalClaim = "family_name" },
                new DefaultClaimMapping { ExternalClaim = "picture", LocalClaim = "picture" },
                new DefaultClaimMapping { ExternalClaim = "locale", LocalClaim = "locale" }
            ]
        };

        // Okta (Tier 2)
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.Okta,
            DisplayName = "Okta",
            Description = "Sign in with Okta.",
            AuthorityPattern = "https://{domain}.okta.com",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.Okta,
            BrandColor = "#007dc1",
            DocumentationUrl = "https://developer.okta.com/docs/guides/implement-oauth-for-okta/main/",
            Tier = 2,
            AuthorityPlaceholders =
            [
                new AuthorityPlaceholder
                {
                    Name = "domain",
                    Label = "Okta Domain",
                    HelpText = "Your Okta organization subdomain (without .okta.com).",
                    Required = true,
                    Placeholder = "dev-123456"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" }
            ]
        };

        // Auth0 (Tier 2)
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.Auth0,
            DisplayName = "Auth0",
            Description = "Sign in with Auth0.",
            AuthorityPattern = "https://{tenant}.auth0.com",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.Auth0,
            BrandColor = "#eb5424",
            DocumentationUrl = "https://auth0.com/docs/authenticate/protocols/openid-connect-protocol",
            Tier = 2,
            AuthorityPlaceholders =
            [
                new AuthorityPlaceholder
                {
                    Name = "tenant",
                    Label = "Auth0 Tenant",
                    HelpText = "Your Auth0 tenant name (without .auth0.com).",
                    Required = true,
                    Placeholder = "my-tenant"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" }
            ]
        };

        // Keycloak (Tier 2)
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.Keycloak,
            DisplayName = "Keycloak",
            Description = "Sign in with Keycloak.",
            AuthorityPattern = "https://{host}/realms/{realm}",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.Keycloak,
            BrandColor = "#4d4d4d",
            DocumentationUrl = "https://www.keycloak.org/docs/latest/securing_apps/",
            Tier = 2,
            AuthorityPlaceholders =
            [
                new AuthorityPlaceholder
                {
                    Name = "host",
                    Label = "Keycloak Host",
                    HelpText = "Your Keycloak server hostname.",
                    Required = true,
                    Placeholder = "keycloak.example.com"
                },
                new AuthorityPlaceholder
                {
                    Name = "realm",
                    Label = "Realm",
                    HelpText = "The Keycloak realm name.",
                    Required = true,
                    Placeholder = "my-realm"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "preferred_username", LocalClaim = "preferred_username" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" }
            ]
        };

        // AWS Cognito (Tier 2)
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.AwsCognito,
            DisplayName = "AWS Cognito",
            Description = "Sign in with Amazon Cognito User Pools.",
            AuthorityPattern = "https://cognito-idp.{region}.amazonaws.com/{userPoolId}",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.AwsCognito,
            BrandColor = "#ff9900",
            DocumentationUrl = "https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-userpools-server-contract-reference.html",
            Tier = 2,
            AuthorityPlaceholders =
            [
                new AuthorityPlaceholder
                {
                    Name = "region",
                    Label = "AWS Region",
                    HelpText = "The AWS region of your user pool.",
                    Required = true,
                    Placeholder = "us-east-1"
                },
                new AuthorityPlaceholder
                {
                    Name = "userPoolId",
                    Label = "User Pool ID",
                    HelpText = "Your Cognito User Pool ID.",
                    Required = true,
                    Placeholder = "us-east-1_xxxxxxxxx"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "cognito:username", LocalClaim = "preferred_username" }
            ]
        };

        // Ping Identity (Tier 2)
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.PingIdentity,
            DisplayName = "Ping Identity",
            Description = "Sign in with Ping Identity.",
            AuthorityPattern = "https://{host}",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.PingIdentity,
            BrandColor = "#b8232f",
            DocumentationUrl = "https://docs.pingidentity.com/",
            Tier = 2,
            AuthorityPlaceholders =
            [
                new AuthorityPlaceholder
                {
                    Name = "host",
                    Label = "Ping Identity Host",
                    HelpText = "Your Ping Identity server hostname.",
                    Required = true,
                    Placeholder = "auth.pingone.com/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/as"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" }
            ]
        };

        // OneLogin (Tier 2)
        yield return new ProviderTemplateDefinition
        {
            Template = WellKnownProviderTemplate.OneLogin,
            DisplayName = "OneLogin",
            Description = "Sign in with OneLogin.",
            AuthorityPattern = "https://{subdomain}.onelogin.com/oidc/2",
            DefaultScopes = ["openid", "profile", "email"],
            IconSvg = ProviderIcons.OneLogin,
            BrandColor = "#37474f",
            DocumentationUrl = "https://developers.onelogin.com/openid-connect",
            Tier = 2,
            AuthorityPlaceholders =
            [
                new AuthorityPlaceholder
                {
                    Name = "subdomain",
                    Label = "OneLogin Subdomain",
                    HelpText = "Your OneLogin subdomain (without .onelogin.com).",
                    Required = true,
                    Placeholder = "my-company"
                }
            ],
            DefaultClaimMappings =
            [
                new DefaultClaimMapping { ExternalClaim = "sub", LocalClaim = "sub" },
                new DefaultClaimMapping { ExternalClaim = "email", LocalClaim = "email" },
                new DefaultClaimMapping { ExternalClaim = "name", LocalClaim = "name" },
                new DefaultClaimMapping { ExternalClaim = "preferred_username", LocalClaim = "preferred_username" }
            ]
        };
    }
}
