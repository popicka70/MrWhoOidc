using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Registrations;

[AllowAnonymous]
public class IndexModel(AuthDbContext db, IPasswordHasher hasher, ITenantAccessor tenantAccessor, IRegistrationService registrationService) : PageModel
{
    public List<SelectListItem> ClientOptions { get; private set; } = new();

    [BindProperty]
    public RegistrationInput Input { get; set; } = new();

    public string? SuccessMessage { get; private set; }
    public string? InfoMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadClientsAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadClientsAsync();
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
            var userId = await registrationService.CreateAndMaybeApproveRegistrationAsync(
                email: Input.Email.Trim(),
                firstName: string.IsNullOrWhiteSpace(Input.FirstName) ? null : Input.FirstName.Trim(),
                lastName: string.IsNullOrWhiteSpace(Input.LastName) ? null : Input.LastName.Trim(),
                clientId: Input.ClientId,
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

    private async Task LoadClientsAsync()
    {
        var currentTenant = tenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            ClientOptions = new List<SelectListItem>();
            return;
        }

        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.TenantId == currentTenant.TenantId)
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { c.Id, c.ClientId, RealmName = r.Name })
            .OrderBy(x => x.ClientId).ToListAsync();
        ClientOptions = clients.Select(c => new SelectListItem($"{c.ClientId} ({c.RealmName})", c.Id.ToString())).ToList();
    }

    public sealed class RegistrationInput
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = string.Empty;
        [StringLength(100)]
        public string? FirstName { get; set; }
        [StringLength(100)]
        public string? LastName { get; set; }
        public Guid? ClientId { get; set; }
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
