using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class EditModel(
    AuthDbContext db,
    IIdentityProviderValidator validator,
    IHttpClientFactory httpClientFactory,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    IOptions<OidcOptions> oidcOptions) : PageModel
{
    [BindProperty]
    public InputModel? Input { get; set; }

    [BindProperty]
    public OidcConfigForm? OidcConfig { get; set; }

    [BindProperty]
    public IFormFile? Logo { get; set; }

    public List<SelectListItem> TypeOptions { get; private set; } = new();

    public bool DiscoveryOk { get; private set; }
    public string? DiscoverySummary { get; private set; }
    public string? DiscoveryJson { get; private set; }

    /// <summary>
    /// The redirect URI(s) that the external IDP should be configured with.
    /// This is computed based on the current tenant context and multi-tenancy settings.
    /// </summary>
    public List<string> RedirectUris { get; private set; } = new();

    /// <summary>
    /// The logout callback URI(s) that the external IDP should be configured with for federated logout.
    /// This is computed based on the current tenant context and multi-tenancy settings.
    /// </summary>
    public List<string> LogoutCallbackUris { get; private set; } = new();

    /// <summary>
    /// Validates that the current user has access to the provider based on tenant filtering.
    /// </summary>
    private async Task<bool> ValidateTenantAccessAsync(Guid providerId)
    {
        var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false; // No tenant context
        }

        // Check if provider belongs to the current tenant
        return await db.IdentityProviders.AnyAsync(p => p.Id == providerId && p.TenantId == currentTenantId.Value);
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await ValidateTenantAccessAsync(id))
        {
            return NotFound();
        }

        await LoadTypesAsync();
        var entity = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (entity is null)
            return NotFound();

        Input = new InputModel
        {
            Id = entity.Id,
            Name = entity.Name,
            DisplayName = entity.DisplayName,
            Type = entity.Type,
            Enabled = entity.Enabled,
            IsDefault = entity.IsDefault,
            AllowRegistration = entity.AllowRegistration,
            SortOrder = entity.SortOrder,
            LogoUrl = entity.LogoUrl,
            LogoData = entity.LogoData,
            ConfigJson = entity.ConfigJson
        };

        // Parse JSON to form if OIDC
        if (entity.Type == IdentityProviderType.Oidc)
        {
            if (!string.IsNullOrWhiteSpace(entity.ConfigJson))
            {
                OidcConfig = JsonToForm(entity.ConfigJson);

                if (OidcConfig is not null && OidcProviderConfigJsonMerger.TryExtractExtendedJson(entity.ConfigJson, out var extendedJson, out _))
                {
                    OidcConfig.ExtendedJson = extendedJson;
                }
            }
            else
            {
                // Create default OIDC configuration if none exists
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
        }

        // Compute redirect URIs for display
        ComputeRedirectUris();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!await ValidateTenantAccessAsync(id))
        {
            return NotFound();
        }

        await LoadTypesAsync();
        if (!ModelState.IsValid || Input is null)
            return Page();

        // guard: route id must match posted entity id
        if (Input.Id != id)
        {
            ModelState.AddModelError(string.Empty, "Mismatched id.");
            return Page();
        }

        var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
        if (entity is null)
            return NotFound();

        entity.Name = Input.Name.Trim();
        entity.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim();
        entity.Type = Input.Type;
        entity.Enabled = Input.Enabled;
        entity.IsDefault = Input.IsDefault;
        entity.AllowRegistration = Input.AllowRegistration;
        entity.SortOrder = Input.SortOrder;
        entity.LogoUrl = string.IsNullOrWhiteSpace(Input.LogoUrl) ? null : Input.LogoUrl.Trim();

        if (Input.Type == IdentityProviderType.Oidc && OidcConfig != null && !string.IsNullOrWhiteSpace(OidcConfig.Authority))
        {
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

            var overwriteClientSecret = !string.IsNullOrWhiteSpace(OidcConfig.ClientSecret);
            var extendedJson = string.IsNullOrWhiteSpace(OidcConfig.ExtendedJson) ? null : OidcConfig.ExtendedJson;

            if (!OidcProviderConfigJsonMerger.TryMerge(
                    existingJson: entity.ConfigJson,
                    standardConfig: standardCfg,
                    extendedJson: extendedJson,
                    overwriteClientSecret: overwriteClientSecret,
                    mergedJson: out var mergedJson,
                    error: out var mergeError))
            {
                ModelState.AddModelError("OidcConfig.ExtendedJson", mergeError ?? "Invalid extended configuration.");
                return Page();
            }

            entity.ConfigJson = mergedJson;
        }
        else
        {
            // Basic JSON validation (non-OIDC types, or OIDC fallback when the form is not usable)
            if (!string.IsNullOrWhiteSpace(Input.ConfigJson))
            {
                try { using var _ = JsonDocument.Parse(Input.ConfigJson); }
                catch (Exception ex)
                {
                    ModelState.AddModelError("Input.ConfigJson", $"Invalid JSON: {ex.Message}");
                    return Page();
                }
            }

            entity.ConfigJson = string.IsNullOrWhiteSpace(Input.ConfigJson) ? null : Input.ConfigJson.Trim();
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;

        var (ok, error) = await validator.ValidateAsync(entity);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, error ?? "Invalid configuration");
            return Page();
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            ModelState.AddModelError(string.Empty, "Conflict: entity was changed by another user. Reload and try again.");
            return Page();
        }

        TempData["Success"] = "Provider updated.";
        
        // Build tenant-aware redirect URL
        var redirectUrl = TenantAwareUrlBuilder.BuildTenantPath(
            "/admin/providers",
            tenantAccessor,
            multiTenancyOptions);
        return Redirect(redirectUrl);
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id)
    {
        if (!await ValidateTenantAccessAsync(id))
        {
            return NotFound();
        }

        await LoadTypesAsync();
        if (Input is null)
            return Page();

        if (Input.Type != IdentityProviderType.Oidc || string.IsNullOrWhiteSpace(Input.ConfigJson))
        {
            DiscoveryOk = false;
            DiscoverySummary = "Only OIDC providers with a config can be tested.";
            return Page();
        }

        try
        {
            using var doc = JsonDocument.Parse(Input.ConfigJson);
            var el = doc.RootElement;
            string? authority = el.TryGetProperty("Authority", out var au) ? au.GetString() : null;
            string? discovery = el.TryGetProperty("DiscoveryUrl", out var du) ? du.GetString() : null;
            if (string.IsNullOrWhiteSpace(authority))
            {
                DiscoveryOk = false;
                DiscoverySummary = "Authority missing in config.";
                return Page();
            }
            var url = string.IsNullOrWhiteSpace(discovery) ? authority.TrimEnd('/') + "/.well-known/openid-configuration" : discovery!;

            var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var json = await http.GetStringAsync(url);

            // Pretty-print subset
            using var meta = JsonDocument.Parse(json);
            var root = meta.RootElement;
            var excerpt = new
            {
                issuer = root.TryGetProperty("issuer", out var iss) ? iss.GetString() : null,
                authorization_endpoint = root.TryGetProperty("authorization_endpoint", out var a) ? a.GetString() : null,
                token_endpoint = root.TryGetProperty("token_endpoint", out var t) ? t.GetString() : null,
                jwks_uri = root.TryGetProperty("jwks_uri", out var j) ? j.GetString() : null,
                response_types_supported = root.TryGetProperty("response_types_supported", out var rts) ? rts : default,
                id_token_signing_alg_values_supported = root.TryGetProperty("id_token_signing_alg_values_supported", out var idalgs) ? idalgs : default
            };
            DiscoveryJson = JsonSerializer.Serialize(excerpt, new JsonSerializerOptions { WriteIndented = true });
            DiscoveryOk = true;
            DiscoverySummary = $"Discovery OK: {url}";
        }
        catch (Exception ex)
        {
            DiscoveryOk = false;
            DiscoverySummary = "Discovery failed: " + ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostUploadLogoAsync(Guid id)
    {
        await LoadTypesAsync();
        var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
        if (entity is null) return NotFound();

        if (Logo is null || Logo.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Select a logo file.");
            // Reload the input model for rendering
            Input = new InputModel
            {
                Id = entity.Id,
                Name = entity.Name,
                DisplayName = entity.DisplayName,
                Type = entity.Type,
                Enabled = entity.Enabled,
                IsDefault = entity.IsDefault,
                SortOrder = entity.SortOrder,
                LogoUrl = entity.LogoUrl,
                ConfigJson = entity.ConfigJson
            };
            return Page();
        }

        // Validate file: size <= 512KB, allowed extensions
        var maxBytes = 512 * 1024;
        if (Logo.Length > maxBytes)
        {
            ModelState.AddModelError(string.Empty, "Logo too large (max 512 KB).");
            Input = new InputModel
            {
                Id = entity.Id,
                Name = entity.Name,
                DisplayName = entity.DisplayName,
                Type = entity.Type,
                Enabled = entity.Enabled,
                IsDefault = entity.IsDefault,
                SortOrder = entity.SortOrder,
                LogoUrl = entity.LogoUrl,
                ConfigJson = entity.ConfigJson
            };
            return Page();
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".svg", ".webp" };
        var ext = Path.GetExtension(Logo.FileName);
        if (string.IsNullOrWhiteSpace(ext) || !allowed.Contains(ext))
        {
            ModelState.AddModelError(string.Empty, "Unsupported file type. Allowed: .png, .jpg, .jpeg, .svg, .webp");
            Input = new InputModel
            {
                Id = entity.Id,
                Name = entity.Name,
                DisplayName = entity.DisplayName,
                Type = entity.Type,
                Enabled = entity.Enabled,
                IsDefault = entity.IsDefault,
                SortOrder = entity.SortOrder,
                LogoUrl = entity.LogoUrl,
                ConfigJson = entity.ConfigJson
            };
            return Page();
        }

        // Store logo in database instead of file system
        using var ms = new MemoryStream();
        await Logo.CopyToAsync(ms);
        entity.LogoData = ms.ToArray();
        entity.LogoContentType = Logo.ContentType ?? GetContentType(ext);
        
        // Set LogoUrl to the database-served endpoint
        var version = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        entity.LogoUrl = $"/api/providers/{entity.Id}/logo?v={version}";
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Repopulate Input for rendering
        Input = new InputModel
        {
            Id = entity.Id,
            Name = entity.Name,
            DisplayName = entity.DisplayName,
            Type = entity.Type,
            Enabled = entity.Enabled,
            IsDefault = entity.IsDefault,
            SortOrder = entity.SortOrder,
            LogoUrl = entity.LogoUrl,
            ConfigJson = entity.ConfigJson
        };

        TempData["Success"] = "Logo uploaded.";
        return Page();
    }

    public async Task<IActionResult> OnPostClearLogoAsync(Guid id)
    {
        await LoadTypesAsync();
        var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
        if (entity is null) return NotFound();
        entity.LogoUrl = null;
        entity.LogoData = null;
        entity.LogoContentType = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Input = new InputModel
        {
            Id = entity.Id,
            Name = entity.Name,
            DisplayName = entity.DisplayName,
            Type = entity.Type,
            Enabled = entity.Enabled,
            IsDefault = entity.IsDefault,
            SortOrder = entity.SortOrder,
            LogoUrl = entity.LogoUrl,
            ConfigJson = entity.ConfigJson
        };
        TempData["Success"] = "Logo cleared.";
        return Page();
    }

    private Task LoadTypesAsync()
    {
        TypeOptions = new()
        {
            new SelectListItem("OIDC", ((int)IdentityProviderType.Oidc).ToString()),
            new SelectListItem("SAML", ((int)IdentityProviderType.Saml).ToString())
        };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Computes the redirect URI(s) that should be configured in the external IDP.
    /// Takes into account the current tenant context and multi-tenancy settings.
    /// </summary>
    private void ComputeRedirectUris()
    {
        RedirectUris.Clear();
        LogoutCallbackUris.Clear();

        // Use canonical tenant-aware issuer (may include /t/{slug})
        var baseUrl = HttpContext.GetIssuer(oidcOptions.Value);

        // The callback paths
        var authCallbackPath = "/auth/external/callback";
        var logoutCallbackPath = "/logout/federated-callback";

        RedirectUris.Add($"{baseUrl}{authCallbackPath}");
        LogoutCallbackUris.Add($"{baseUrl}{logoutCallbackPath}");
    }

    private OidcConfigForm? JsonToForm(string json)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<OidcProviderConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (cfg is null) return null;

            return new OidcConfigForm
            {
                Authority = cfg.Authority,
                DiscoveryUrl = cfg.DiscoveryUrl,
                ClientId = cfg.ClientId,
                ClientSecret = null,
                ResponseType = cfg.ResponseType,
                ScopesString = string.Join(" ", cfg.Scopes ?? Array.Empty<string>()),
                UsePKCE = cfg.UsePKCE,
                UseJAR = cfg.UseJAR,
                UsePAR = cfg.UsePAR,
                RequestedAcrValues = cfg.RequestedAcrValues,
                Prompt = cfg.Prompt,
                ResponseMode = cfg.ResponseMode,
                ClockSkewSeconds = cfg.ClockSkewSeconds,
                ValidateIssuer = cfg.TokenValidation?.ValidateIssuer ?? true,
                ValidateAudience = cfg.TokenValidation?.ValidateAudience ?? false,
                ValidateLifetime = cfg.TokenValidation?.ValidateLifetime ?? true,
                BackChannelLogout = cfg.BackChannelLogout,
                ExtraAuthParamsJson = cfg.ExtraAuthParams != null ? JsonSerializer.Serialize(cfg.ExtraAuthParams) : null
            };
        }
        catch
        {
            return null;
        }
    }

    private string? FormToJson(OidcConfigForm form)
    {
        try
        {
            var scopes = string.IsNullOrWhiteSpace(form.ScopesString)
                ? new[] { "openid", "profile", "email" }
                : form.ScopesString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Dictionary<string, string>? extraParams = null;
            if (!string.IsNullOrWhiteSpace(form.ExtraAuthParamsJson))
            {
                extraParams = JsonSerializer.Deserialize<Dictionary<string, string>>(form.ExtraAuthParamsJson);
            }

            var cfg = new OidcProviderConfig
            {
                Authority = form.Authority,
                DiscoveryUrl = string.IsNullOrWhiteSpace(form.DiscoveryUrl) ? null : form.DiscoveryUrl,
                ClientId = form.ClientId,
                ClientSecret = string.IsNullOrWhiteSpace(form.ClientSecret) ? null : form.ClientSecret,
                ResponseType = form.ResponseType,
                Scopes = scopes,
                UsePKCE = form.UsePKCE,
                UseJAR = form.UseJAR,
                UsePAR = form.UsePAR,
                RequestedAcrValues = string.IsNullOrWhiteSpace(form.RequestedAcrValues) ? null : form.RequestedAcrValues,
                Prompt = string.IsNullOrWhiteSpace(form.Prompt) ? null : form.Prompt,
                ResponseMode = string.IsNullOrWhiteSpace(form.ResponseMode) ? null : form.ResponseMode,
                ClockSkewSeconds = form.ClockSkewSeconds,
                TokenValidation = new TokenValidationOptions
                {
                    ValidateIssuer = form.ValidateIssuer,
                    ValidateAudience = form.ValidateAudience,
                    ValidateLifetime = form.ValidateLifetime
                },
                BackChannelLogout = form.BackChannelLogout,
                ExtraAuthParams = extraParams
            };

            return JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return null;
        }
    }

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? DisplayName { get; set; }
        public IdentityProviderType Type { get; set; } = IdentityProviderType.Oidc;
        public bool Enabled { get; set; } = true;
        public bool IsDefault { get; set; } = false;
        /// <summary>
        /// When true, this IdP appears on the public registration page allowing users to register via external authentication.
        /// Only applicable for IdPs in the default tenant.
        /// </summary>
        public bool AllowRegistration { get; set; } = false;
        public int SortOrder { get; set; } = 0;
        [Url]
        public string? LogoUrl { get; set; }
        public byte[]? LogoData { get; set; }
        public string? ConfigJson { get; set; }
    }

    /// <summary>
    /// Gets the MIME content type for a file extension.
    /// </summary>
    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };
}
