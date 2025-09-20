namespace MrWhoOidc.WebAuth.Handlers;

public sealed class OidcOptions
{
    public string? Issuer { get; set; }
    public string[] AllowedPostLogoutRedirectUris { get; set; } = [];
}
