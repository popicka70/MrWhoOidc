using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class AddModel(
    AuthDbContext db,
    IIdentityProviderValidator validator,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public OidcConfigForm? OidcConfig { get; set; }

    [BindProperty]
    public IFormFile? Logo { get; set; }

    /// <summary>
    /// Selected provider template (0 = Custom, 1 = Entra, etc.)
    /// </summary>
    [BindProperty]
    public int ProviderTemplate { get; set; }

    /// <summary>
    /// Authority URL placeholders for template-based providers.
    /// </summary>
    [BindProperty]
    public Dictionary<string, string> AuthorityPlaceholders { get; set; } = new();

    /// <summary>
    /// Provider-specific config fields.
    /// </summary>
    [BindProperty]
    public Dictionary<string, string> ProviderConfigFields { get; set; } = new();

    /// <summary>
    /// Entra ID specific configuration.
    /// </summary>
    [BindProperty]
    public EntraConfigInput? EntraConfig { get; set; }

    /// <summary>
    /// Google specific configuration.
    /// </summary>
    [BindProperty]
    public GoogleConfigInput? GoogleConfig { get; set; }

    /// <summary>
    /// Facebook specific configuration.
    /// </summary>
    [BindProperty]
    public FacebookConfigInput? FacebookConfig { get; set; }

    /// <summary>
    /// Apple specific configuration.
    /// </summary>
    [BindProperty]
    public AppleConfigInput? AppleConfig { get; set; }

    /// <summary>
    /// GitHub specific configuration.
    /// </summary>
    [BindProperty]
    public GitHubConfigInput? GitHubConfig { get; set; }

    public List<SelectListItem> TypeOptions { get; private set; } = new();

    public void OnGet()
    {
        LoadTypes();

        OidcConfig = new OidcConfigForm
        {
            Authority = string.Empty,
            ClientId = string.Empty,
            ResponseType = "code",
            ScopesString = "openid profile email",
            UsePKCE = true,
            UseJAR = false,
            UsePAR = false,
            ClockSkewSeconds = 120,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            BackChannelLogout = true
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        LoadTypes();

        // Only validate OIDC form when the provider type is OIDC.
        if (Input.Type != IdentityProviderType.Oidc)
        {
            var keys = ModelState.Keys.Where(k => k.StartsWith("OidcConfig.", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in keys)
            {
                ModelState.Remove(k);
            }
        }

        // Remove template-specific validation errors for unselected templates
        RemoveUnusedTemplateValidationErrors();

        if (!ModelState.IsValid)
            return Page();

        // Process template-specific configuration
        var selectedTemplate = (WellKnownProviderTemplate)ProviderTemplate;
        string? providerSpecificJson = null;

        if (selectedTemplate != WellKnownProviderTemplate.Custom)
        {
            // Apply template defaults and build provider-specific config
            var templateDef = WellKnownProviderCatalog.GetTemplate(selectedTemplate);
            if (templateDef != null)
            {
                // Build authority URL from placeholders if needed
                if (OidcConfig != null && templateDef.AuthorityPlaceholders.Length > 0 && string.IsNullOrWhiteSpace(OidcConfig.Authority))
                {
                    OidcConfig.Authority = BuildAuthorityFromTemplate(templateDef);
                }

                // Apply template defaults to OidcConfig
                ApplyTemplateDefaults(templateDef);

                // Build provider-specific JSON
                providerSpecificJson = BuildProviderSpecificJson(selectedTemplate);
            }
        }

        if (Input.Type == IdentityProviderType.Oidc)
        {
            if (OidcConfig is null)
            {
                ModelState.AddModelError(string.Empty, "OIDC configuration is required.");
                return Page();
            }

            var scopes = string.IsNullOrWhiteSpace(OidcConfig.ScopesString)
                ? new[] { "openid", "profile", "email" }
                : OidcConfig.ScopesString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Dictionary<string, string>? extraParams = null;
            if (!string.IsNullOrWhiteSpace(OidcConfig.ExtraAuthParamsJson))
            {
                try
                {
                    extraParams = JsonSerializer.Deserialize<Dictionary<string, string>>(OidcConfig.ExtraAuthParamsJson);
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("OidcConfig.ExtraAuthParamsJson", $"Invalid JSON: {ex.Message}");
                    return Page();
                }
            }

            // Add provider-specific extra params
            extraParams = AddProviderSpecificParams(selectedTemplate, extraParams);

            var standardCfg = new OidcProviderConfig
            {
                Authority = OidcConfig.Authority,
                DiscoveryUrl = string.IsNullOrWhiteSpace(OidcConfig.DiscoveryUrl) ? null : OidcConfig.DiscoveryUrl,
                ClientId = OidcConfig.ClientId,
                ClientSecret = string.IsNullOrWhiteSpace(OidcConfig.ClientSecret) ? null : OidcConfig.ClientSecret,
                ResponseType = OidcConfig.ResponseType,
                Scopes = scopes,
                UsePKCE = OidcConfig.UsePKCE,
                UseJAR = OidcConfig.UseJAR,
                UsePAR = OidcConfig.UsePAR,
                RequestedAcrValues = string.IsNullOrWhiteSpace(OidcConfig.RequestedAcrValues) ? null : OidcConfig.RequestedAcrValues,
                Prompt = string.IsNullOrWhiteSpace(OidcConfig.Prompt) ? null : OidcConfig.Prompt,
                ResponseMode = string.IsNullOrWhiteSpace(OidcConfig.ResponseMode) ? null : OidcConfig.ResponseMode,
                ClockSkewSeconds = OidcConfig.ClockSkewSeconds,
                TokenValidation = new TokenValidationOptions
                {
                    ValidateIssuer = OidcConfig.ValidateIssuer,
                    ValidateAudience = OidcConfig.ValidateAudience,
                    ValidateLifetime = OidcConfig.ValidateLifetime
                },
                BackChannelLogout = OidcConfig.BackChannelLogout,
                ExtraAuthParams = extraParams
            };

            if (!OidcProviderConfigJsonMerger.TryMerge(
                    existingJson: null,
                    standardConfig: standardCfg,
                    extendedJson: OidcConfig.ExtendedJson,
                    overwriteClientSecret: true,
                    mergedJson: out var mergedJson,
                    error: out var mergeError))
            {
                ModelState.AddModelError("OidcConfig.ExtendedJson", mergeError ?? "Invalid extended configuration.");
                return Page();
            }

            Input.ConfigJson = mergedJson;
        }
        else
        {
            // Basic JSON validation (non-OIDC types)
            if (!string.IsNullOrWhiteSpace(Input.ConfigJson))
            {
                try
                {
                    using var _ = JsonDocument.Parse(Input.ConfigJson);
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("Input.ConfigJson", $"Invalid JSON: {ex.Message}");
                    return Page();
                }
            }
        }

        // Get current tenant ID from context
        var currentTenant = tenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            ModelState.AddModelError(string.Empty, "Unable to determine current tenant context");
            return Page();
        }

        var entity = new IdentityProvider
        {
            TenantId = currentTenant.TenantId,
            Name = Input.Name.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim(),
            Type = Input.Type,
            ProviderTemplate = selectedTemplate != WellKnownProviderTemplate.Custom ? selectedTemplate : null,
            Enabled = Input.Enabled,
            IsDefault = Input.IsDefault,
            AllowRegistration = Input.AllowRegistration,
            SortOrder = Input.SortOrder,
            LogoUrl = string.IsNullOrWhiteSpace(Input.LogoUrl) ? null : Input.LogoUrl.Trim(),
            LogoStorageType = string.IsNullOrWhiteSpace(Input.LogoUrl)
                ? IdentityProviderLogoStorageType.None
                : IdentityProviderLogoStorageType.ExternalUrl,
            ConfigJson = string.IsNullOrWhiteSpace(Input.ConfigJson) ? null : Input.ConfigJson.Trim(),
            ProviderSpecificConfigJson = providerSpecificJson,
            ButtonBackgroundColor = string.IsNullOrWhiteSpace(Input.ButtonBackgroundColor) ? null : Input.ButtonBackgroundColor.Trim(),
            ButtonTextColor = string.IsNullOrWhiteSpace(Input.ButtonTextColor) ? null : Input.ButtonTextColor.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Handle logo upload if provided
        if (Logo != null && Logo.Length > 0)
        {
            // Validate file: size <= 512KB, allowed extensions
            var maxBytes = 512 * 1024;
            if (Logo.Length > maxBytes)
            {
                ModelState.AddModelError(string.Empty, "Logo too large (max 512 KB).");
                return Page();
            }

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
            var ext = Path.GetExtension(Logo.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !allowed.Contains(ext))
            {
                ModelState.AddModelError(string.Empty, "Unsupported file type. Allowed: .png, .jpg, .jpeg, .webp");
                return Page();
            }

            using var ms = new MemoryStream();
            await Logo.CopyToAsync(ms);
            entity.LogoData = ms.ToArray();
            entity.LogoContentType = Logo.ContentType ?? GetContentType(ext);

            entity.LogoStorageType = IdentityProviderLogoStorageType.Database;
            entity.LogoUrl = null;
        }
        else if (string.IsNullOrWhiteSpace(entity.LogoUrl) && selectedTemplate != WellKnownProviderTemplate.Custom)
        {
            // Use preset icon from template if no file uploaded and no URL provided
            var templateDef = WellKnownProviderCatalog.GetTemplate(selectedTemplate);
            if (templateDef != null && !string.IsNullOrEmpty(templateDef.IconSvg))
            {
                entity.LogoData = System.Text.Encoding.UTF8.GetBytes(templateDef.IconSvg);
                entity.LogoContentType = "image/svg+xml";

                entity.LogoStorageType = IdentityProviderLogoStorageType.Database;
                entity.LogoUrl = null;
            }
        }

        var (ok, error) = await validator.ValidateAsync(entity);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, error ?? "Invalid configuration");
            return Page();
        }

        db.IdentityProviders.Add(entity);

        // Add default claim mappings for well-known providers
        if (selectedTemplate != WellKnownProviderTemplate.Custom)
        {
            var templateDef = WellKnownProviderCatalog.GetTemplate(selectedTemplate);
            if (templateDef != null)
            {
                var order = 0;
                foreach (var mapping in templateDef.DefaultClaimMappings)
                {
                    db.IdentityProviderClaimMappings.Add(new IdentityProviderClaimMapping
                    {
                        IdentityProviderId = entity.Id,
                        ExternalClaim = mapping.ExternalClaim,
                        LocalClaim = mapping.LocalClaim,
                        Transform = mapping.Transform,
                        Order = order++
                    });
                }
            }
        }

        await db.SaveChangesAsync();

        // Build tenant-aware redirect URL
        var redirectUrl = TenantAwareUrlBuilder.BuildTenantPath(
            "/Admin/Providers",
            tenantAccessor,
            multiTenancyOptions);
        return Redirect(redirectUrl);
    }

    private void LoadTypes()
    {
        TypeOptions = new()
        {
            new SelectListItem("OIDC", ((int)IdentityProviderType.Oidc).ToString()),
            new SelectListItem("SAML", ((int)IdentityProviderType.Saml).ToString())
        };
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private void RemoveUnusedTemplateValidationErrors()
    {
        var prefixesToRemove = new List<string>();
        var selectedTemplate = (WellKnownProviderTemplate)ProviderTemplate;

        // Only keep validation for the selected template's config
        if (selectedTemplate != WellKnownProviderTemplate.MicrosoftEntraId)
            prefixesToRemove.Add("EntraConfig.");
        if (selectedTemplate != WellKnownProviderTemplate.Google)
            prefixesToRemove.Add("GoogleConfig.");
        if (selectedTemplate != WellKnownProviderTemplate.Facebook)
            prefixesToRemove.Add("FacebookConfig.");
        if (selectedTemplate != WellKnownProviderTemplate.Apple)
            prefixesToRemove.Add("AppleConfig.");
        if (selectedTemplate != WellKnownProviderTemplate.GitHub)
            prefixesToRemove.Add("GitHubConfig.");

        foreach (var prefix in prefixesToRemove)
        {
            var keys = ModelState.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in keys)
            {
                ModelState.Remove(k);
            }
        }
    }

    private string BuildAuthorityFromTemplate(ProviderTemplateDefinition templateDef)
    {
        var authority = templateDef.AuthorityPattern;

        // Handle Entra ID special case
        if (templateDef.Template == WellKnownProviderTemplate.MicrosoftEntraId && EntraConfig != null)
        {
            var tenant = EntraConfig.TenantType switch
            {
                "specific" when !string.IsNullOrWhiteSpace(EntraConfig.TenantId) => EntraConfig.TenantId,
                "organizations" => "organizations",
                "consumers" => "consumers",
                _ => "common"
            };
            return $"https://login.microsoftonline.com/{tenant}/v2.0";
        }

        // Replace placeholders from AuthorityPlaceholders dictionary
        foreach (var placeholder in templateDef.AuthorityPlaceholders)
        {
            if (AuthorityPlaceholders.TryGetValue(placeholder.Name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                authority = authority.Replace($"{{{placeholder.Name}}}", value);
            }
            else if (!string.IsNullOrWhiteSpace(placeholder.DefaultValue))
            {
                authority = authority.Replace($"{{{placeholder.Name}}}", placeholder.DefaultValue);
            }
        }

        return authority;
    }

    private void ApplyTemplateDefaults(ProviderTemplateDefinition templateDef)
    {
        if (OidcConfig == null) return;

        // Apply default scopes if not already set
        if (string.IsNullOrWhiteSpace(OidcConfig.ScopesString))
        {
            OidcConfig.ScopesString = string.Join(" ", templateDef.DefaultScopes);
        }

        // Apply default response type
        if (string.IsNullOrWhiteSpace(OidcConfig.ResponseType))
        {
            OidcConfig.ResponseType = templateDef.ResponseType;
        }

        // Apply PKCE setting
        OidcConfig.UsePKCE = templateDef.DefaultUsePkce;

        // Special handling for providers with fixed authorities
        if (string.IsNullOrWhiteSpace(OidcConfig.Authority))
        {
            switch (templateDef.Template)
            {
                case WellKnownProviderTemplate.Google:
                    OidcConfig.Authority = "https://accounts.google.com";
                    break;
                case WellKnownProviderTemplate.Apple:
                    OidcConfig.Authority = "https://appleid.apple.com";
                    OidcConfig.ResponseMode = "form_post";
                    break;
                case WellKnownProviderTemplate.GitHub:
                    OidcConfig.Authority = "https://github.com";
                    break;
                case WellKnownProviderTemplate.Facebook:
                    OidcConfig.Authority = "https://www.facebook.com";
                    break;
                case WellKnownProviderTemplate.LinkedIn:
                    OidcConfig.Authority = "https://www.linkedin.com/oauth";
                    break;
            }
        }
    }

    private Dictionary<string, string>? AddProviderSpecificParams(WellKnownProviderTemplate template, Dictionary<string, string>? existingParams)
    {
        existingParams ??= new Dictionary<string, string>();

        switch (template)
        {
            case WellKnownProviderTemplate.MicrosoftEntraId when EntraConfig != null:
                if (!string.IsNullOrWhiteSpace(EntraConfig.DomainHint))
                    existingParams["domain_hint"] = EntraConfig.DomainHint;
                if (!string.IsNullOrWhiteSpace(EntraConfig.LoginHint))
                    existingParams["login_hint"] = EntraConfig.LoginHint;
                if (!string.IsNullOrWhiteSpace(EntraConfig.Prompt))
                    OidcConfig!.Prompt = EntraConfig.Prompt;
                break;

            case WellKnownProviderTemplate.Google when GoogleConfig != null:
                if (!string.IsNullOrWhiteSpace(GoogleConfig.HostedDomain))
                    existingParams["hd"] = GoogleConfig.HostedDomain;
                if (!string.IsNullOrWhiteSpace(GoogleConfig.LoginHint))
                    existingParams["login_hint"] = GoogleConfig.LoginHint;
                if (!string.IsNullOrWhiteSpace(GoogleConfig.AccessType) && GoogleConfig.AccessType != "online")
                    existingParams["access_type"] = GoogleConfig.AccessType;
                if (!string.IsNullOrWhiteSpace(GoogleConfig.Prompt))
                    OidcConfig!.Prompt = GoogleConfig.Prompt;
                break;
        }

        return existingParams.Count > 0 ? existingParams : null;
    }

    private string? BuildProviderSpecificJson(WellKnownProviderTemplate template)
    {
        object? config = template switch
        {
            WellKnownProviderTemplate.MicrosoftEntraId when EntraConfig != null => new EntraIdProviderConfig
            {
                TenantType = EntraConfig.TenantType ?? "common",
                TenantId = EntraConfig.TenantId,
                DomainHint = EntraConfig.DomainHint,
                LoginHint = EntraConfig.LoginHint,
                Prompt = EntraConfig.Prompt
            },
            WellKnownProviderTemplate.Google when GoogleConfig != null => new GoogleProviderConfig
            {
                HostedDomain = GoogleConfig.HostedDomain,
                LoginHint = GoogleConfig.LoginHint,
                Prompt = GoogleConfig.Prompt,
                AccessType = GoogleConfig.AccessType ?? "online"
            },
            WellKnownProviderTemplate.Facebook when FacebookConfig != null => new FacebookProviderConfig
            {
                ApiVersion = FacebookConfig.ApiVersion ?? "v19.0",
                EnableReauthorization = FacebookConfig.EnableReauthorization
            },
            WellKnownProviderTemplate.Apple when AppleConfig != null => new AppleProviderConfig
            {
                TeamId = AppleConfig.TeamId ?? string.Empty,
                KeyId = AppleConfig.KeyId ?? string.Empty,
                PrivateKey = AppleConfig.PrivateKey ?? string.Empty
            },
            WellKnownProviderTemplate.GitHub when GitHubConfig != null => new GitHubProviderConfig
            {
                AllowedOrganizations = GitHubConfig.AllowedOrganizations,
                AllowedTeams = GitHubConfig.AllowedTeams,
                AllowPrivateEmails = GitHubConfig.AllowPrivateEmails
            },
            _ => null
        };

        if (config == null) return null;

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = false });
    }

    public sealed class InputModel
    {
        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? DisplayName { get; set; }
        public IdentityProviderType Type { get; set; } = IdentityProviderType.Oidc;
        public bool Enabled { get; set; } = true;
        public bool IsDefault { get; set; } = false;
        public bool AllowRegistration { get; set; } = false;
        public int SortOrder { get; set; } = 0;
        [Url]
        public string? LogoUrl { get; set; }
        public string? ConfigJson { get; set; }

        [StringLength(20)]
        public string? ButtonBackgroundColor { get; set; }

        [StringLength(20)]
        public string? ButtonTextColor { get; set; }
    }

    public sealed class EntraConfigInput
    {
        public string? TenantType { get; set; }
        public string? TenantId { get; set; }
        public string? DomainHint { get; set; }
        public string? LoginHint { get; set; }
        public string? Prompt { get; set; }
    }

    public sealed class GoogleConfigInput
    {
        public string? HostedDomain { get; set; }
        public string? LoginHint { get; set; }
        public string? Prompt { get; set; }
        public string? AccessType { get; set; }
    }

    public sealed class FacebookConfigInput
    {
        public string? ApiVersion { get; set; }
        public bool EnableReauthorization { get; set; }
    }

    public sealed class AppleConfigInput
    {
        public string? TeamId { get; set; }
        public string? KeyId { get; set; }
        public string? PrivateKey { get; set; }
    }

    public sealed class GitHubConfigInput
    {
        public string? AllowedOrganizations { get; set; }
        public string? AllowedTeams { get; set; }
        public bool AllowPrivateEmails { get; set; } = true;
    }
}
