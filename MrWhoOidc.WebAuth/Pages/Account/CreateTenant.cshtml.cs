using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class CreateTenantModel(ITenantService tenantService, ITenantSwitchingService tenantSwitchingService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [StringLength(200, MinimumLength = 3, ErrorMessage = "Tenant name must be between 3 and 200 characters.")]
        [Display(Name = "Organization Name")]
        public string Name { get; set; } = string.Empty;

        [StringLength(TenantSlug.MaxLength)]
        [RegularExpression("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
            ErrorMessage = "The slug may only contain lowercase letters, digits and hyphens, and may not start or end with a hyphen.")]
        [Display(Name = "URL Slug")]
        public string? Slug { get; set; }
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdStr, out var userId))
        {
            return Forbid();
        }

        // Only pass a custom slug through when the user actually entered one; blank means
        // "generate a unique one for me".
        var customSlug = string.IsNullOrWhiteSpace(Input.Slug) ? null : Input.Slug.Trim();

        try
        {
            var tenant = await tenantService.CreateTenantAsync(Input.Name, userId, customSlug);

            // Switch context to the new tenant
            await tenantSwitchingService.SwitchTenantAsync(HttpContext, tenant.Id);

            // Redirect to the new tenant's dashboard
            // The URL structure is usually /t/{slug}/account
            return Redirect($"/t/{tenant.Slug}/account");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("slug", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Slug)}", ex.Message);
            return Page();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("name", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Name)}", ex.Message);
            return Page();
        }
        catch (ArgumentException)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Slug)}",
                "Invalid slug format. Slugs must be 1-63 characters, lowercase letters, digits, and hyphens only.");
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        catch (Exception)
        {
            ModelState.AddModelError(string.Empty, "An unexpected error occurred while creating the organization.");
            return Page();
        }
    }
}
