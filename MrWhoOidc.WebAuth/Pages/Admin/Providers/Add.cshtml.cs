using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class AddModel(AuthDbContext db, IIdentityProviderValidator validator) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public List<SelectListItem> TypeOptions { get; private set; } = new();

    public void OnGet()
    {
        TypeOptions = new()
        {
            new SelectListItem("OIDC", ((int)IdentityProviderType.Oidc).ToString()),
            new SelectListItem("SAML", ((int)IdentityProviderType.Saml).ToString())
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        OnGet();
        if (!ModelState.IsValid)
            return Page();

        // Basic JSON validation
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

        var entity = new IdentityProvider
        {
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
        return RedirectToPage("Index");
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
