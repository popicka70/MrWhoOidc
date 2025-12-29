namespace MrWhoOidc.Auth.IdentityProviders;

/// <summary>
/// Well-known identity provider templates for simplified configuration.
/// Provides pre-configured settings for popular OIDC identity providers.
/// </summary>
public enum WellKnownProviderTemplate
{
    /// <summary>
    /// Generic OIDC provider - requires full manual configuration.
    /// </summary>
    Custom = 0,

    /// <summary>
    /// Microsoft Entra ID (formerly Azure AD).
    /// Supports multi-tenant configurations (common, organizations, consumers, specific tenant).
    /// </summary>
    MicrosoftEntraId = 1,

    /// <summary>
    /// Google Identity Platform.
    /// Supports hosted domain (hd) parameter for Google Workspace restrictions.
    /// </summary>
    Google = 2,

    /// <summary>
    /// Facebook Login (Meta).
    /// Uses OIDC with API versioning.
    /// </summary>
    Facebook = 3,

    /// <summary>
    /// Sign in with Apple.
    /// Requires private key JWT for client authentication.
    /// </summary>
    Apple = 4,

    /// <summary>
    /// GitHub OAuth.
    /// Note: GitHub is OAuth2-based with limited OIDC support.
    /// </summary>
    GitHub = 5,

    /// <summary>
    /// LinkedIn OpenID Connect.
    /// OpenID Connect certified provider.
    /// </summary>
    LinkedIn = 6,

    /// <summary>
    /// Okta Identity Platform.
    /// Supports custom domains.
    /// </summary>
    Okta = 7,

    /// <summary>
    /// Auth0 Identity Platform.
    /// Tenant-based authority configuration.
    /// </summary>
    Auth0 = 8,

    /// <summary>
    /// Keycloak Open Source Identity.
    /// Realm-based authority configuration.
    /// </summary>
    Keycloak = 9,

    /// <summary>
    /// AWS Cognito User Pools.
    /// Region and user pool ID based configuration.
    /// </summary>
    AwsCognito = 10,

    /// <summary>
    /// Ping Identity.
    /// Enterprise identity provider.
    /// </summary>
    PingIdentity = 11,

    /// <summary>
    /// OneLogin.
    /// Subdomain-based authority configuration.
    /// </summary>
    OneLogin = 12
}
