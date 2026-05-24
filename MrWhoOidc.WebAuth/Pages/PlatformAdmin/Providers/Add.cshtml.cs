using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Providers;

[Authorize(Policy = "platform-admin")]
public sealed class AddModel(AuthDbContext db, IIdentityProviderValidator validator) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var entity = new IdentityProvider
        {
            TenantId = null,
            Name = Input.Name.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim(),
            Type = IdentityProviderType.Oidc,
            Enabled = Input.Enabled,
            IsDefault = Input.IsDefault,
            SortOrder = Input.SortOrder,
            ConfigJson = BuildOidcConfigJson(Input),
            ButtonBackgroundColor = string.IsNullOrWhiteSpace(Input.ButtonBackgroundColor) ? null : Input.ButtonBackgroundColor.Trim(),
            ButtonTextColor = string.IsNullOrWhiteSpace(Input.ButtonTextColor) ? null : Input.ButtonTextColor.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (ok, error) = await validator.ValidateAsync(entity);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, error ?? "Invalid provider configuration.");
            return Page();
        }

        db.IdentityProviders.Add(entity);
        await db.SaveChangesAsync();

        TempData["SuccessMessage"] = "Platform provider created.";
        return RedirectToPage("/PlatformAdmin/Providers/Index");
    }

    internal static string BuildOidcConfigJson(InputModel input)
    {
        var scopes = string.IsNullOrWhiteSpace(input.Scopes)
            ? new[] { "openid", "profile", "email" }
            : input.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var config = new OidcProviderConfig
        {
            Authority = input.Authority.Trim().TrimEnd('/'),
            DiscoveryUrl = string.IsNullOrWhiteSpace(input.DiscoveryUrl) ? null : input.DiscoveryUrl.Trim(),
            ClientId = input.ClientId.Trim(),
            ClientSecret = string.IsNullOrWhiteSpace(input.ClientSecret) ? null : input.ClientSecret,
            ResponseType = "code",
            Scopes = scopes,
            UsePKCE = true,
            UseJAR = false,
            UsePAR = false,
            ClockSkewSeconds = 120,
            TokenValidation = new TokenValidationOptions
            {
                ValidateIssuer = true,
                ValidateAudience = false,
                ValidateLifetime = true
            },
            BackChannelLogout = true,
            ExtraAuthParams = new Dictionary<string, string>()
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    public sealed class InputModel
    {
        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;

        [StringLength(200)]
        public string? DisplayName { get; set; }

        public bool Enabled { get; set; } = true;

        public bool IsDefault { get; set; }

        public int SortOrder { get; set; }

        [Required, Url]
        public string Authority { get; set; } = string.Empty;

        [Url]
        public string? DiscoveryUrl { get; set; }

        [Required]
        public string ClientId { get; set; } = string.Empty;

        public string? ClientSecret { get; set; }

        public string Scopes { get; set; } = "openid profile email";

        [StringLength(20)]
        public string? ButtonBackgroundColor { get; set; }

        [StringLength(20)]
        public string? ButtonTextColor { get; set; }
    }
}