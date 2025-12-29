namespace MrWhoOidc.Auth.IdentityProviders;

/// <summary>
/// SVG icons for well-known identity providers.
/// Icons are embedded as strings for easy use in Razor views.
/// </summary>
public static class ProviderIcons
{
    /// <summary>
    /// Custom/Generic OIDC icon (gear symbol).
    /// </summary>
    public const string Custom = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="provider-icon">
            <circle cx="12" cy="12" r="3"/>
            <path d="M12 1v2m0 18v2M4.22 4.22l1.42 1.42m12.72 12.72l1.42 1.42M1 12h2m18 0h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/>
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z"/>
        </svg>
        """;

    /// <summary>
    /// Microsoft logo (simplified).
    /// </summary>
    public const string Microsoft = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 23 23" class="provider-icon">
            <path fill="#f35325" d="M1 1h10v10H1z"/>
            <path fill="#81bc06" d="M12 1h10v10H12z"/>
            <path fill="#05a6f0" d="M1 12h10v10H1z"/>
            <path fill="#ffba08" d="M12 12h10v10H12z"/>
        </svg>
        """;

    /// <summary>
    /// Google "G" logo.
    /// </summary>
    public const string Google = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" class="provider-icon">
            <path fill="#FFC107" d="M43.611,20.083H42V20H24v8h11.303c-1.649,4.657-6.08,8-11.303,8c-6.627,0-12-5.373-12-12c0-6.627,5.373-12,12-12c3.059,0,5.842,1.154,7.961,3.039l5.657-5.657C34.046,6.053,29.268,4,24,4C12.955,4,4,12.955,4,24c0,11.045,8.955,20,20,20c11.045,0,20-8.955,20-20C44,22.659,43.862,21.35,43.611,20.083z"/>
            <path fill="#FF3D00" d="M6.306,14.691l6.571,4.819C14.655,15.108,18.961,12,24,12c3.059,0,5.842,1.154,7.961,3.039l5.657-5.657C34.046,6.053,29.268,4,24,4C16.318,4,9.656,8.337,6.306,14.691z"/>
            <path fill="#4CAF50" d="M24,44c5.166,0,9.86-1.977,13.409-5.192l-6.19-5.238C29.211,35.091,26.715,36,24,36c-5.202,0-9.619-3.317-11.283-7.946l-6.522,5.025C9.505,39.556,16.227,44,24,44z"/>
            <path fill="#1976D2" d="M43.611,20.083H42V20H24v8h11.303c-0.792,2.237-2.231,4.166-4.087,5.571c0.001-0.001,0.002-0.001,0.003-0.002l6.19,5.238C36.971,39.205,44,34,44,24C44,22.659,43.862,21.35,43.611,20.083z"/>
        </svg>
        """;

    /// <summary>
    /// Facebook "f" logo.
    /// </summary>
    public const string Facebook = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" class="provider-icon">
            <path fill="#1877F2" d="M24 4C12.954 4 4 12.954 4 24c0 9.983 7.314 18.27 16.875 19.757V29.5h-5.078V24h5.078v-4.198c0-5.012 2.986-7.779 7.553-7.779 2.189 0 4.477.39 4.477.39v4.922h-2.522c-2.484 0-3.258 1.541-3.258 3.122V24h5.547l-.887 5.5h-4.66v14.257C36.686 42.27 44 33.983 44 24 44 12.954 35.046 4 24 4z"/>
            <path fill="#fff" d="M29.66 29.5l.887-5.5h-5.547v-3.543c0-1.581.774-3.122 3.258-3.122h2.522v-4.922s-2.288-.39-4.477-.39c-4.567 0-7.553 2.767-7.553 7.779V24h-5.078v5.5h5.078v14.257a20.174 20.174 0 006.25 0V29.5h4.66z"/>
        </svg>
        """;

    /// <summary>
    /// Apple logo.
    /// </summary>
    public const string Apple = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <path fill="currentColor" d="M18.71 19.5c-.83 1.24-1.71 2.45-3.05 2.47-1.34.03-1.77-.79-3.29-.79-1.53 0-2 .77-3.27.82-1.31.05-2.3-1.32-3.14-2.53C4.25 17 2.94 12.45 4.7 9.39c.87-1.52 2.43-2.48 4.12-2.51 1.28-.02 2.5.87 3.29.87.78 0 2.26-1.07 3.81-.91.65.03 2.47.26 3.64 1.98-.09.06-2.17 1.28-2.15 3.81.03 3.02 2.65 4.03 2.68 4.04-.03.07-.42 1.44-1.38 2.83M13 3.5c.73-.83 1.94-1.46 2.94-1.5.13 1.17-.34 2.35-1.04 3.19-.69.85-1.83 1.51-2.95 1.42-.15-1.15.41-2.35 1.05-3.11z"/>
        </svg>
        """;

    /// <summary>
    /// GitHub Octocat logo.
    /// </summary>
    public const string GitHub = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <path fill="currentColor" d="M12 2A10 10 0 0 0 2 12c0 4.42 2.87 8.17 6.84 9.5.5.08.66-.23.66-.5v-1.69c-2.77.6-3.36-1.34-3.36-1.34-.46-1.16-1.11-1.47-1.11-1.47-.91-.62.07-.6.07-.6 1 .07 1.53 1.03 1.53 1.03.87 1.52 2.34 1.07 2.91.83.09-.65.35-1.09.63-1.34-2.22-.25-4.55-1.11-4.55-4.92 0-1.11.38-2 1.03-2.71-.1-.25-.45-1.29.1-2.64 0 0 .84-.27 2.75 1.02.79-.22 1.65-.33 2.5-.33.85 0 1.71.11 2.5.33 1.91-1.29 2.75-1.02 2.75-1.02.55 1.35.2 2.39.1 2.64.65.71 1.03 1.6 1.03 2.71 0 3.82-2.34 4.66-4.57 4.91.36.31.69.92.69 1.85V21c0 .27.16.59.67.5C19.14 20.16 22 16.42 22 12A10 10 0 0 0 12 2z"/>
        </svg>
        """;

    /// <summary>
    /// LinkedIn "in" logo.
    /// </summary>
    public const string LinkedIn = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" class="provider-icon">
            <path fill="#0288D1" d="M42,37c0,2.762-2.238,5-5,5H11c-2.761,0-5-2.238-5-5V11c0-2.762,2.239-5,5-5h26c2.762,0,5,2.238,5,5V37z"/>
            <path fill="#FFF" d="M12 19H17V36H12zM14.485 17h-.028C12.965 17 12 15.888 12 14.499 12 13.08 12.995 12 14.514 12c1.521 0 2.458 1.08 2.486 2.499C17 15.887 16.035 17 14.485 17zM36 36h-5v-9.099c0-2.198-1.225-3.698-3.192-3.698-1.501 0-2.313 1.012-2.707 1.99C24.957 25.543 25 26.511 25 27v9h-5V19h5v2.616C25.721 20.5 26.85 19 29.738 19c3.578 0 6.261 2.25 6.261 7.274L36 36 36 36z"/>
        </svg>
        """;

    /// <summary>
    /// Okta logo (simplified).
    /// </summary>
    public const string Okta = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <circle fill="#007dc1" cx="12" cy="12" r="10"/>
            <circle fill="#fff" cx="12" cy="12" r="4"/>
        </svg>
        """;

    /// <summary>
    /// Auth0 logo (simplified).
    /// </summary>
    public const string Auth0 = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <path fill="#eb5424" d="M21.98 7.448L19.62 0H4.347L2.02 7.448c-1.352 4.312.03 9.206 3.815 12.015L12.007 24l6.157-4.552c3.755-2.81 5.182-7.688 3.815-12.015l-6.16 4.58 2.343 7.45-6.157-4.597-6.158 4.58 2.358-7.433-6.188-4.55 7.633-.045L12.008 0l2.356 7.404 7.615.044z"/>
        </svg>
        """;

    /// <summary>
    /// Keycloak logo (simplified).
    /// </summary>
    public const string Keycloak = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <path fill="#4d4d4d" d="M12 2L2 7v10l10 5 10-5V7L12 2zm0 2.18l7.66 3.82L12 11.82 4.34 8 12 4.18zM4 9.45l7 3.5v7.1l-7-3.5v-7.1zm9 10.6v-7.1l7-3.5v7.1l-7 3.5z"/>
        </svg>
        """;

    /// <summary>
    /// AWS Cognito logo (simplified).
    /// </summary>
    public const string AwsCognito = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <path fill="#ff9900" d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z"/>
            <path fill="#ff9900" d="M12 6c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6-2.69-6-6-6zm0 10c-2.21 0-4-1.79-4-4s1.79-4 4-4 4 1.79 4 4-1.79 4-4 4z"/>
            <circle fill="#ff9900" cx="12" cy="12" r="2"/>
        </svg>
        """;

    /// <summary>
    /// Ping Identity logo (simplified).
    /// </summary>
    public const string PingIdentity = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <path fill="#b8232f" d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 15H9V9h2v8zm4 0h-2V9h2v8z"/>
        </svg>
        """;

    /// <summary>
    /// OneLogin logo (simplified).
    /// </summary>
    public const string OneLogin = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <rect fill="#37474f" x="2" y="2" width="20" height="20" rx="4"/>
            <text x="12" y="16" text-anchor="middle" fill="#fff" font-size="10" font-weight="bold">1</text>
        </svg>
        """;

    /// <summary>
    /// Generic OIDC logo.
    /// </summary>
    public const string CustomOidc = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="provider-icon">
            <path fill="#6c757d" d="M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 10.99h7c-.53 4.12-3.28 7.79-7 8.94V12H5V6.3l7-3.11v8.8z"/>
        </svg>
        """;

    /// <summary>
    /// QR Code icon for login.
    /// </summary>
    public const string QrCode = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="provider-icon">
            <rect x="3" y="3" width="7" height="7"></rect>
            <rect x="14" y="3" width="7" height="7"></rect>
            <rect x="14" y="14" width="7" height="7"></rect>
            <rect x="3" y="14" width="7" height="7"></rect>
            <line x1="7" y1="7" x2="7" y2="7"></line>
            <line x1="17" y1="7" x2="17" y2="7"></line>
            <line x1="17" y1="17" x2="17" y2="17"></line>
            <line x1="7" y1="17" x2="7" y2="17"></line>
        </svg>
        """;

    /// <summary>
    /// Gets the SVG icon for a provider template.
    /// </summary>
    public static string GetIcon(WellKnownProviderTemplate template) => template switch
    {
        WellKnownProviderTemplate.MicrosoftEntraId => Microsoft,
        WellKnownProviderTemplate.Google => Google,
        WellKnownProviderTemplate.Facebook => Facebook,
        WellKnownProviderTemplate.Apple => Apple,
        WellKnownProviderTemplate.GitHub => GitHub,
        WellKnownProviderTemplate.LinkedIn => LinkedIn,
        WellKnownProviderTemplate.Okta => Okta,
        WellKnownProviderTemplate.Auth0 => Auth0,
        WellKnownProviderTemplate.Keycloak => Keycloak,
        WellKnownProviderTemplate.AwsCognito => AwsCognito,
        WellKnownProviderTemplate.PingIdentity => PingIdentity,
        WellKnownProviderTemplate.OneLogin => OneLogin,
        _ => CustomOidc
    };
}
