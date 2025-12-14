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
            ValidateAudience = false,
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

        if (!ModelState.IsValid)
            return Page();

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
            Enabled = Input.Enabled,
            IsDefault = Input.IsDefault,
            SortOrder = Input.SortOrder,
            LogoUrl = string.IsNullOrWhiteSpace(Input.LogoUrl) ? null : Input.LogoUrl.Trim(),
            ConfigJson = string.IsNullOrWhiteSpace(Input.ConfigJson) ? null : Input.ConfigJson.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (ok, error) = await validator.ValidateAsync(entity);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, error ?? "Invalid configuration");
            return Page();
        }

        db.IdentityProviders.Add(entity);
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

    public sealed class InputModel
    {
        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? DisplayName { get; set; }
        public IdentityProviderType Type { get; set; } = IdentityProviderType.Oidc;
        public bool Enabled { get; set; } = true;
        public bool IsDefault { get; set; } = false;
        public int SortOrder { get; set; } = 0;
        [Url]
        public string? LogoUrl { get; set; }
        public string? ConfigJson { get; set; }
    }
}
