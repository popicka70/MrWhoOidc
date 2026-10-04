namespace MrWhoOidc.KeyGen.Configuration;

/// <summary>
/// OpenID Connect sign-in settings for the KeyGen admin UI (section <c>KeyGen:Auth</c>).
/// </summary>
/// <remarks>
/// KeyGen mints signed licenses and private JWKs, so it fails closed: outside Development the
/// app refuses to start unless <see cref="Authority"/> and <see cref="ClientId"/> are set.
/// In Development, <see cref="DisableInDevelopment"/> must be set explicitly to run without an IdP.
/// </remarks>
public sealed class KeyGenAuthOptions
{
    public const string SectionName = "KeyGen:Auth";

    public const string DefaultRequiredRole = "platform-admin";

    public const string DefaultRoleClaimType = "roles";

    /// <summary>OIDC issuer URL (HTTPS), e.g. https://idp.example.com/t/default.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Optional. Leave empty for a public client (PKCE only). Supply via env/vault, never appsettings.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Role every user must hold to use KeyGen.</summary>
    public string RequiredRole { get; set; } = DefaultRequiredRole;

    /// <summary>Claim type carrying roles in the id_token/userinfo. MrWhoOidc emits <c>roles</c>.</summary>
    public string RoleClaimType { get; set; } = DefaultRoleClaimType;

    /// <summary>Scopes to request. <c>openid</c> is always included.</summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email", "roles"];

    /// <summary>
    /// Development only: run without an IdP. Every request is signed in as a fixed local
    /// developer identity. In any other environment the app refuses to start when this is set.
    /// </summary>
    public bool DisableInDevelopment { get; set; }

    /// <summary>
    /// Returns configuration errors that must stop startup, or an empty list when the
    /// options are usable in the current environment.
    /// </summary>
    public IReadOnlyList<string> Validate(bool isDevelopment)
    {
        var errors = new List<string>();

        if (DisableInDevelopment)
        {
            if (!isDevelopment)
            {
                errors.Add($"{SectionName}:DisableInDevelopment=true is only allowed in the Development environment.");
            }

            return errors;
        }

        if (string.IsNullOrWhiteSpace(Authority))
        {
            errors.Add($"{SectionName}:Authority is required.");
        }
        else if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{SectionName}:Authority must be an absolute https:// URL.");
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            errors.Add($"{SectionName}:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(RequiredRole))
        {
            errors.Add($"{SectionName}:RequiredRole must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(RoleClaimType))
        {
            errors.Add($"{SectionName}:RoleClaimType must not be empty.");
        }

        return errors;
    }
}
