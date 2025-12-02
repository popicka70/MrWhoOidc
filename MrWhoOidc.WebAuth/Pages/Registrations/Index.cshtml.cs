using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Registrations;

[AllowAnonymous]
public class IndexModel(IPasswordHasher hasher, IRegistrationService registrationService) : PageModel
{
    [BindProperty]
    public RegistrationInput Input { get; set; } = new();

    public string? SuccessMessage { get; private set; }
    public string? InfoMessage { get; private set; }

    public void OnGet()
    {
        // No async work needed - client loading removed for security
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            string? passwordHash = null;
            if (!string.IsNullOrWhiteSpace(Input.Password))
            {
                passwordHash = hasher.Hash(Input.Password);
            }

            // Determine tenant creation parameters
            string? tenantSlug = null;
            string? tenantName = null;
            string? tenantDescription = null;

            if (Input.CreateTenant)
            {
                tenantSlug = Input.TenantSlug?.Trim().ToLowerInvariant();
                tenantName = Input.TenantName?.Trim();
                tenantDescription = Input.TenantDescription?.Trim();

                // Validate tenant fields when creating tenant
                if (string.IsNullOrWhiteSpace(tenantSlug))
                {
                    ModelState.AddModelError(nameof(Input.TenantSlug), "Tenant slug is required.");
                    return Page();
                }
                if (string.IsNullOrWhiteSpace(tenantName))
                {
                    ModelState.AddModelError(nameof(Input.TenantName), "Tenant name is required.");
                    return Page();
                }
            }

            // Use the registration service instead of direct DB operations
            // Note: clientId is always null - client assignment is done post-registration by admins
            var userId = await registrationService.CreateAndMaybeApproveRegistrationAsync(
                email: Input.Email.Trim(),
                firstName: string.IsNullOrWhiteSpace(Input.FirstName) ? null : Input.FirstName.Trim(),
                lastName: string.IsNullOrWhiteSpace(Input.LastName) ? null : Input.LastName.Trim(),
                clientId: null,
                passwordHash: passwordHash,
                isExternalIdp: false, // Local registration
                autoApprove: true, // Auto-approve tenant admin registrations
                tenantSlug: tenantSlug,
                tenantName: tenantName,
                tenantDescription: tenantDescription);

            if (userId.HasValue)
            {
                SuccessMessage = Input.CreateTenant
                    ? $"Registration successful! You've been automatically approved as the tenant admin for '{tenantName}'. Please check your email for confirmation instructions."
                    : "Registration successful! Please check your email for confirmation instructions.";
            }
            else
            {
                InfoMessage = "Registration submitted. You'll be notified when it's approved.";
            }

            ModelState.Clear();
            Input = new();
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
        catch (Exception)
        {
            ModelState.AddModelError(string.Empty, "An error occurred during registration. Please try again.");
        }

        return Page();
    }

    public sealed class RegistrationInput
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = string.Empty;
        [StringLength(100)]
        public string? FirstName { get; set; }
        [StringLength(100)]
        public string? LastName { get; set; }
        [StringLength(200)]
        [DataType(DataType.Password)]
        public string? Password { get; set; }

        // New: Tenant creation options
        public bool CreateTenant { get; set; }
        [StringLength(100)]
        public string? TenantSlug { get; set; }
        [StringLength(200)]
        public string? TenantName { get; set; }
        [StringLength(500)]
        public string? TenantDescription { get; set; }
    }
}
