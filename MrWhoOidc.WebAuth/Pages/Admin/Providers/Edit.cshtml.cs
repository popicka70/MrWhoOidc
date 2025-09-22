using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "admin")]
public class EditModel(AuthDbContext db, IIdentityProviderValidator validator) : PageModel
{
    [BindProperty]
    public InputModel? Input { get; set; }

    public List<SelectListItem> TypeOptions { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        TypeOptions = new()
        {
            new SelectListItem("OIDC", ((int)IdentityProviderType.Oidc).ToString()),
            new SelectListItem("SAML", ((int)IdentityProviderType.Saml).ToString())
        };

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
        // ensure TypeOptions for redisplay
        TypeOptions = new()
        {
            new SelectListItem("OIDC", ((int)IdentityProviderType.Oidc).ToString()),
            new SelectListItem("SAML", ((int)IdentityProviderType.Saml).ToString())
        };

        if (!ModelState.IsValid || Input is null)
            return Page();

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

        await db.SaveChangesAsync();
        return RedirectToPage("Index");
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
