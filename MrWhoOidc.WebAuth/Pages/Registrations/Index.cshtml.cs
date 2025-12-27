using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Registrations;

/// <summary>
/// Represents an external identity provider option available for registration.
/// </summary>
public sealed record RegistrationIdpOption
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? LogoUrl { get; init; }
}

[AllowAnonymous]
public class IndexModel(
    IPasswordHasher hasher,
    IRegistrationWorkflowService registrationService,
    IReturnUrlClientContextResolver clientContextResolver,
    AuthDbContext dbContext,
    IMultiTenancyOptions multiTenancyOptions,
    ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    public RegistrationInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// When returning from IdP callback, indicates flow mode.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Mode { get; set; }

    public string? SuccessMessage { get; private set; }
    public string? InfoMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// External identity providers available for registration.
    /// </summary>
    public List<RegistrationIdpOption> RegistrationIdps { get; private set; } = [];

    public async Task OnGetAsync()
    {
        // Handle IdP callback mode
        if (Mode == "idp_callback")
        {
            SuccessMessage = "Registration successful! Please check your email for confirmation instructions.";
        }
        else if (Mode == "idp_duplicate")
        {
            ErrorMessage = "An account with this email already exists. Please sign in instead.";
        }

        // Load registration-enabled IdPs from default tenant
        await LoadRegistrationIdpsAsync();
    }

    private async Task LoadRegistrationIdpsAsync()
    {
        try
        {
            // Get the default tenant slug from configuration
            var defaultTenantSlug = multiTenancyOptions.DefaultTenantSlug ?? "default";

            // Get the default tenant ID
            var defaultTenantId = await dbContext.Tenants
                .AsNoTracking()
                .Where(t => t.Slug == defaultTenantSlug && t.Status == TenantStatus.Active)
                .Select(t => t.Id)
                .FirstOrDefaultAsync();

            if (defaultTenantId == Guid.Empty)
            {
                logger.LogWarning("No default tenant found for registration IdP loading (slug: {Slug})", defaultTenantSlug);
                return;
            }

            // Load IdPs that are enabled and allow registration
            RegistrationIdps = await dbContext.IdentityProviders
                .AsNoTracking()
                .Where(p => p.TenantId == defaultTenantId
                         && p.Enabled
                         && p.AllowRegistration)
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.DisplayName)
                .Select(p => new RegistrationIdpOption
                {
                    Name = p.Name,
                    DisplayName = p.DisplayName ?? p.Name,
                    LogoUrl = p.LogoUrl
                })
                .ToListAsync();

            logger.LogDebug("Loaded {Count} registration-enabled IdPs for registration page", RegistrationIdps.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load registration IdPs");
            // Don't throw - page should still render with manual registration option
        }
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
            // Only associate a client when we can derive it from a validated authorize context and the client opts in.
            // Auto-approve only when creating a new tenant (user becomes tenant admin)
            Guid? clientId = null;
            if (!Input.CreateTenant)
            {
                Client? client = await clientContextResolver.TryResolveClientAsync(HttpContext, ReturnUrl, HttpContext.RequestAborted);
                if (client is not null && client.AutoAssignNewUsersToClient)
                {
                    clientId = client.Id;
                }
            }

            var userId = await registrationService.CreateAndMaybeApproveRegistrationAsync(
                email: Input.Email.Trim(),
                firstName: string.IsNullOrWhiteSpace(Input.FirstName) ? null : Input.FirstName.Trim(),
                lastName: string.IsNullOrWhiteSpace(Input.LastName) ? null : Input.LastName.Trim(),
                clientId: clientId,
                passwordHash: passwordHash,
                isExternalIdp: false, // Local registration
                autoApprove: Input.CreateTenant, // Only auto-approve tenant admin registrations
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
