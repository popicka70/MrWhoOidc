using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

public sealed class OidcConfigForm
{
    [Required, Url]
    [Display(Name = "Authority")]
    public string Authority { get; set; } = string.Empty;

    [Url]
    [Display(Name = "Discovery URL (optional)")]
    public string? DiscoveryUrl { get; set; }

    [Required]
    [Display(Name = "Client ID")]
    public string ClientId { get; set; } = string.Empty;

    [Display(Name = "Client Secret")]
    public string? ClientSecret { get; set; }

    [Display(Name = "Response Type")]
    public string ResponseType { get; set; } = "code";

    [Display(Name = "Scopes (space-separated)")]
    public string ScopesString { get; set; } = "openid profile email";

    [Display(Name = "Use PKCE")]
    public bool UsePKCE { get; set; } = true;

    [Display(Name = "Use JAR (JWT-secured Authorization Request)")]
    public bool UseJAR { get; set; } = false;

    [Display(Name = "Use PAR (Pushed Authorization Request)")]
    public bool UsePAR { get; set; } = false;

    [Display(Name = "ACR Values (optional)")]
    public string? RequestedAcrValues { get; set; }

    [Display(Name = "Prompt (optional)")]
    public string? Prompt { get; set; }

    [Display(Name = "Response Mode (optional)")]
    public string? ResponseMode { get; set; }

    [Display(Name = "Clock Skew (seconds)")]
    [Range(0, 600)]
    public int ClockSkewSeconds { get; set; } = 120;

    [Display(Name = "Validate Issuer")]
    public bool ValidateIssuer { get; set; } = true;

    [Display(Name = "Validate Audience")]
    public bool ValidateAudience { get; set; } = false;

    [Display(Name = "Validate Lifetime")]
    public bool ValidateLifetime { get; set; } = true;

    [Display(Name = "Back-Channel Logout")]
    public bool BackChannelLogout { get; set; } = true;

    [Display(Name = "Extra Auth Params (JSON object, optional)")]
    public string? ExtraAuthParamsJson { get; set; }

    [Display(Name = "Extended parameters (JSON object, optional)")]
    public string? ExtendedJson { get; set; }
}
