using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

        try
        {
            var tenant = await tenantService.CreateTenantAsync(Input.Name, userId);
            
            // Switch context to the new tenant
            await tenantSwitchingService.SwitchTenantAsync(HttpContext, tenant.Id);

            // Redirect to the new tenant's dashboard
            // The URL structure is usually /t/{slug}/account
            return Redirect($"/t/{tenant.Slug}/account");
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
