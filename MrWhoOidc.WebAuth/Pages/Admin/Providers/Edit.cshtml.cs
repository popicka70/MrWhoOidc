using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.IO;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "admin")]
public class EditModel(AuthDbContext db, IIdentityProviderValidator validator, IHttpClientFactory httpClientFactory, IWebHostEnvironment env) : PageModel
{
    [BindProperty]
    public InputModel? Input { get; set; }

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
}
