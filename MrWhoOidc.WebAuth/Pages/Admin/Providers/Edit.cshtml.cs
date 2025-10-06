using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.IdentityProviders;
using System.IO;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class EditModel(AuthDbContext db, IIdentityProviderValidator validator, IHttpClientFactory httpClientFactory, IWebHostEnvironment env) : ReadOnlyAdminPageModel
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

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
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
            SortOrder = entity.SortOrder,
            LogoUrl = entity.LogoUrl,
            ConfigJson = entity.ConfigJson
        };

        // Parse JSON to form if OIDC
        if (entity.Type == IdentityProviderType.Oidc && !string.IsNullOrWhiteSpace(entity.ConfigJson))
        {
            OidcConfig = JsonToForm(entity.ConfigJson);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        await LoadTypesAsync();
        if (!ModelState.IsValid || Input is null)
            return Page();

        // guard: route id must match posted entity id
        if (Input.Id != id)
        {
            ModelState.AddModelError(string.Empty, "Mismatched id.");
            return Page();
        }

        // If OIDC and form is populated, convert form to JSON
        // Note: Both form and JSON are submitted, but form takes precedence if populated
        if (Input.Type == IdentityProviderType.Oidc && OidcConfig != null && !string.IsNullOrWhiteSpace(OidcConfig.Authority))
        {
            var json = FormToJson(OidcConfig);
            if (json is null)
            {
                ModelState.AddModelError(string.Empty, "Failed to serialize configuration from form.");
                return Page();
            }
            Input.ConfigJson = json;
        }

        // Basic JSON validation
        if (!string.IsNullOrWhiteSpace(Input.ConfigJson))
        {
            try { using var _ = JsonDocument.Parse(Input.ConfigJson); }
            catch (Exception ex)
            {
                ModelState.AddModelError("Input.ConfigJson", $"Invalid JSON: {ex.Message}");
                return Page();
            }
        }

        var entity = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id);
        if (entity is null)
            return NotFound();

        entity.Name = Input.Name.Trim();
        entity.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim();
        entity.Type = Input.Type;
        entity.Enabled = Input.Enabled;
        entity.IsDefault = Input.IsDefault;
        entity.SortOrder = Input.SortOrder;
        entity.LogoUrl = string.IsNullOrWhiteSpace(Input.LogoUrl) ? null : Input.LogoUrl.Trim();
        entity.ConfigJson = string.IsNullOrWhiteSpace(Input.ConfigJson) ? null : Input.ConfigJson.Trim();
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
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id)
    {
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

        var uploadsDir = Path.Combine(env.WebRootPath, "uploads", "providers");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{entity.Id}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(uploadsDir, fileName);
        using (var stream = System.IO.File.Create(fullPath))
        {
            await Logo.CopyToAsync(stream);
        }
        var version = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        entity.LogoUrl = $"/uploads/providers/{fileName}?v={version}";
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
                ClientSecret = cfg.ClientSecret,
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
        public int SortOrder { get; set; } = 0;
        [Url]
        public string? LogoUrl { get; set; }
        public string? ConfigJson { get; set; }
    }

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
    }
}
