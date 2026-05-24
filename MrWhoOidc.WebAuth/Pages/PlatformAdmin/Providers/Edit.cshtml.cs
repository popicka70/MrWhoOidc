using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Providers;

[Authorize(Policy = "platform-admin")]
public sealed class EditModel(AuthDbContext db, IIdentityProviderValidator validator) : PageModel
{
    [BindProperty]
    public AddModel.InputModel Input { get; set; } = new();

    public Guid ProviderId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.TenantId == null);
        if (provider is null)
        {
            return NotFound();
        }

        ProviderId = provider.Id;
        PopulateInput(provider);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        ProviderId = id;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var provider = await db.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == null);
        if (provider is null)
        {
            return NotFound();
        }

        provider.Name = Input.Name.Trim();
        provider.DisplayName = string.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim();
        provider.Enabled = Input.Enabled;
        provider.IsDefault = Input.IsDefault;
        provider.SortOrder = Input.SortOrder;
        provider.ConfigJson = BuildUpdatedOidcConfigJson(provider.ConfigJson, Input);
        provider.ButtonBackgroundColor = string.IsNullOrWhiteSpace(Input.ButtonBackgroundColor) ? null : Input.ButtonBackgroundColor.Trim();
        provider.ButtonTextColor = string.IsNullOrWhiteSpace(Input.ButtonTextColor) ? null : Input.ButtonTextColor.Trim();
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        var (ok, error) = await validator.ValidateAsync(provider);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, error ?? "Invalid provider configuration.");
            return Page();
        }

        await db.SaveChangesAsync();
        TempData["SuccessMessage"] = "Platform provider updated.";
        return RedirectToPage("/PlatformAdmin/Providers/Index");
    }

    private void PopulateInput(IdentityProvider provider)
    {
        Input = new AddModel.InputModel
        {
            Name = provider.Name,
            DisplayName = provider.DisplayName,
            Enabled = provider.Enabled,
            IsDefault = provider.IsDefault,
            SortOrder = provider.SortOrder,
            ButtonBackgroundColor = provider.ButtonBackgroundColor,
            ButtonTextColor = provider.ButtonTextColor
        };

        if (string.IsNullOrWhiteSpace(provider.ConfigJson))
        {
            return;
        }

        try
        {
            var config = JsonSerializer.Deserialize<OidcProviderConfig>(provider.ConfigJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config is null)
            {
                return;
            }

            Input.Authority = config.Authority;
            Input.DiscoveryUrl = config.DiscoveryUrl;
            Input.ClientId = config.ClientId;
            Input.Scopes = string.Join(" ", config.Scopes ?? Array.Empty<string>());
        }
        catch
        {
        }
    }

    private static string BuildUpdatedOidcConfigJson(string? existingConfigJson, AddModel.InputModel input)
    {
        if (!string.IsNullOrWhiteSpace(input.ClientSecret))
        {
            return AddModel.BuildOidcConfigJson(input);
        }

        var existingSecret = ReadExistingClientSecret(existingConfigJson);
        if (string.IsNullOrWhiteSpace(existingSecret))
        {
            return AddModel.BuildOidcConfigJson(input);
        }

        var merged = new AddModel.InputModel
        {
            Name = input.Name,
            DisplayName = input.DisplayName,
            Enabled = input.Enabled,
            IsDefault = input.IsDefault,
            SortOrder = input.SortOrder,
            Authority = input.Authority,
            DiscoveryUrl = input.DiscoveryUrl,
            ClientId = input.ClientId,
            ClientSecret = existingSecret,
            Scopes = input.Scopes,
            ButtonBackgroundColor = input.ButtonBackgroundColor,
            ButtonTextColor = input.ButtonTextColor
        };

        return AddModel.BuildOidcConfigJson(merged);
    }

    private static string? ReadExistingClientSecret(string? existingConfigJson)
    {
        if (string.IsNullOrWhiteSpace(existingConfigJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OidcProviderConfig>(existingConfigJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })?.ClientSecret;
        }
        catch
        {
            return null;
        }
    }
}