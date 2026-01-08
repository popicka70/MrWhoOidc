using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MrWhoOidc.Auth.Settings;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Security.Admin;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public partial class CreateModel(
    AuthDbContext db,
    IMultiTenancyOptions multiTenancyOptions,
    IHttpContextAccessor httpContextAccessor,
    IOptions<OidcOptions> oidcOptions,
    IUserService userService,
    IUserAccountProvisioner userAccountProvisioner,
    ITenantSwitchingService tenantSwitchingService,
    ILogger<CreateModel> logger) : PageModel
{
    [BindProperty]
    public TenantInput Input { get; set; } = new();

    public string? CurrentUserDisplay { get; private set; }

    public class TenantInput
    {
        [Required]
        [MaxLength(100)]
        [RegularExpression(@"^[a-z0-9\-]+$", ErrorMessage = "Slug must contain only lowercase letters, numbers, and hyphens")]
        public string Slug { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Description { get; set; }

        public TenantStatus Status { get; set; } = TenantStatus.Active;

        [Range(0, int.MaxValue)]
        public int MaxUsers { get; set; } = 10000;

        [Range(0, int.MaxValue)]
        public int MaxClients { get; set; } = 100;

        [MaxLength(200)]
        [Url]
        public string? LogoUrl { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Must be a valid hex color (e.g., #0d6efd)")]
        public string? PrimaryColor { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Must be a valid hex color (e.g., #6610f2)")]
        public string? AccentColor { get; set; }

        [MaxLength(100)]
        public string? BillingPlan { get; set; }
    }

    public IActionResult OnGet()
    {
        // Multi-tenancy must be enabled by license
        if (!multiTenancyOptions.Enabled)
        {
            return RedirectToPage("/PlatformAdmin/Index");
        }

        CaptureCurrentUserDisplay();
        // Set defaults
        Input.Status = TenantStatus.Active;
        Input.MaxUsers = 10000;
        Input.MaxClients = 100;
        Input.BillingPlan = "Free";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Multi-tenancy must be enabled by license
        if (!multiTenancyOptions.Enabled)
        {
            return RedirectToPage("/PlatformAdmin/Index");
        }

        CaptureCurrentUserDisplay();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (await db.Tenants.AnyAsync(t => t.Slug == Input.Slug))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Slug)}", "A tenant with this slug already exists.");
            return Page();
        }

        var creatorUserId = GetCurrentUserId();
        if (creatorUserId is null)
        {
            ModelState.AddModelError(string.Empty, "Unable to resolve the current user. Please sign in again and retry.");
            return Page();
        }

        var creatorUser = await userService.FindByIdAcrossTenantsAsync(creatorUserId.Value);
        if (creatorUser is null)
        {
            ModelState.AddModelError(string.Empty, "Current user record could not be located. Please sign in again.");
            return Page();
        }

        var resolvedEmail = await ResolveCreatorEmailAsync(creatorUser, HttpContext.RequestAborted);
        if (resolvedEmail is null)
        {
            ModelState.AddModelError(string.Empty, "We couldn't find an email address on your account. Please add and verify one before creating a tenant.");
            return Page();
        }

        if (!resolvedEmail.Value.IsVerified)
        {
            ModelState.AddModelError(string.Empty, "Your email address hasn't been verified yet. Please complete verification before creating a tenant.");
            return Page();
        }

        var creatorEmail = resolvedEmail.Value.Email;
        var baseUrl = !string.IsNullOrWhiteSpace(oidcOptions.Value.PublicBaseUrl)
            ? oidcOptions.Value.PublicBaseUrl.TrimEnd('/')
            : $"{httpContextAccessor.HttpContext!.Request.Scheme}://{httpContextAccessor.HttpContext.Request.Host}";

        Tenant provisionedTenant;
        try
        {
            provisionedTenant = await ProvisionTenantAsync(creatorUser, creatorEmail, baseUrl, HttpContext.RequestAborted);
        }
        catch (TenantProvisioningException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }

        var verification = await VerifyProvisionedStateAsync(provisionedTenant.Id, creatorUser.Id, HttpContext.RequestAborted);
        if (!verification.Succeeded)
        {
            logger.LogError("Tenant {TenantId} failed post-commit verification: {Reason}", provisionedTenant.Id, verification.ErrorMessage);
            TempData["ErrorMessage"] = verification.ErrorMessage;
            return RedirectToPage("Index");
        }

        await tenantSwitchingService.SwitchTenantAsync(httpContextAccessor.HttpContext!, provisionedTenant.Id);

        TempData["SuccessMessage"] = $"Tenant '{provisionedTenant.Name}' created successfully! Admin user: {creatorEmail}";
        return RedirectToPage("Index");
    }

    private async Task<Tenant> ProvisionTenantAsync(User creatorUser, string creatorEmail, string baseUrl, CancellationToken ct)
    {
        Tenant? tenant = null;
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                tenant = new Tenant
                {
                    Slug = Input.Slug,
                    Name = Input.Name,
                    Description = Input.Description,
                    IssuerUri = multiTenancyOptions.Enabled
                        ? $"{baseUrl}/t/{Input.Slug}"
                        : baseUrl,
                    Status = Input.Status,
                    MaxUsers = Input.MaxUsers,
                    MaxClients = Input.MaxClients,
                    LogoUrl = Input.LogoUrl,
                    PrimaryColor = Input.PrimaryColor,
                    AccentColor = Input.AccentColor,
                    AdminEmail = creatorEmail,
                    BillingPlan = Input.BillingPlan,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                var defaultRealm = new Realm
                {
                    TenantId = tenant.Id,
                    Name = "default",
                    DisplayName = $"{Input.Name} Default Realm",
                    AllowUnconfirmedLogin = true
                };

                // Default: dynamically registered clients go to the tenant default realm.
                // Setting can be changed/disabled later in Platform Admin tenant edit screen.
                tenant.SettingsJson = System.Text.Json.JsonSerializer.Serialize(
                    new TenantSettings
                    {
                        Auth = new AuthTenantSettings
                        {
                            DynamicClientRegistrationRealmId = defaultRealm.Id
                        }
                    },
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    });

                var adminRealm = new Realm
                {
                    TenantId = tenant.Id,
                    Name = "admin",
                    DisplayName = $"{Input.Name} Admin Realm",
                    AllowUnconfirmedLogin = true
                };

                var tenantAdminRole = new Role
                {
                    Name = "tenant-admin",
                    RealmId = defaultRealm.Id,
                    TenantId = tenant.Id,
                    IsActive = true
                };

                var adminRole = new Role
                {
                    Name = "admin",
                    RealmId = adminRealm.Id,
                    TenantId = tenant.Id,
                    IsActive = true
                };

                var adminClient = new Client
                {
                    ClientId = $"{Input.Slug}-admin",
                    ClientName = $"{Input.Name} Admin Portal",
                    TenantId = tenant.Id,
                    RealmId = adminRealm.Id,
                    RequirePkce = true,
                    RequireConsent = false,
                    AllowedLoginRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                    {
                        $"{baseUrl}/t/{Input.Slug}/signin-oidc"
                    }),
                    AllowedLogoutRedirectUrisJson = System.Text.Json.JsonSerializer.Serialize(new[]
                    {
                        $"{baseUrl}/t/{Input.Slug}/signout-callback-oidc",
                        $"{baseUrl}/t/{Input.Slug}/"
                    })
                };

                var tenantAdminAssignment = new UserRealmRoleAssignment
                {
                    UserId = creatorUser.Id,
                    RoleId = tenantAdminRole.Id,
                    RealmId = defaultRealm.Id,
                    IsActive = true
                };

                var adminAssignment = new UserRealmRoleAssignment
                {
                    UserId = creatorUser.Id,
                    RoleId = adminRole.Id,
                    RealmId = adminRealm.Id,
                    IsActive = true
                };

                // Create a User record in the new tenant for the creator
                // This is required because TenantSwitchingService.GetUserTenantsAsync 
                // queries Users joined with Tenants to find accessible tenants
                var tenantUser = new User
                {
                    // New ID for the tenant-specific user record
                    TenantId = tenant.Id,
                    Username = creatorUser.Username,
                    Email = creatorUser.Email,
                    NormalizedEmail = creatorUser.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(creatorUser.Email ?? string.Empty),
                    Name = creatorUser.Name,
                    EmailVerified = creatorUser.EmailVerified,
                    EmailVerifiedAt = creatorUser.EmailVerifiedAt,
                    TotpEnabled = creatorUser.TotpEnabled,
                    TotpSecret = creatorUser.TotpSecret,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                // Update role assignments to use the new tenant user's ID
                tenantAdminAssignment.UserId = tenantUser.Id;
                adminAssignment.UserId = tenantUser.Id;

                db.Tenants.Add(tenant);
                db.Realms.AddRange(defaultRealm, adminRealm);
                db.Roles.AddRange(tenantAdminRole, adminRole);
                db.Clients.Add(adminClient);
                db.Users.Add(tenantUser);
                db.UserRealmRoleAssignments.AddRange(tenantAdminAssignment, adminAssignment);

                await userAccountProvisioner.EnsureAsync(creatorUser, tenant.Id, defaultRealm.Id, true, ct, autoSave: false);

                await db.SaveChangesAsync(ct);

                var accessibleTenants = await tenantSwitchingService.GetUserTenantsAsync(User);
                if (!accessibleTenants.Any(t => t.TenantId == tenant.Id))
                {
                    throw new TenantProvisioningException("Failed to link your account to the new tenant. Please try again or contact support.");
                }

                await transaction.CommitAsync(ct);
            }
            catch (TenantProvisioningException)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct);
                logger.LogError(ex, "Unexpected error while provisioning tenant {TenantSlug}", Input.Slug);
                throw new TenantProvisioningException("An unexpected error occurred while creating the tenant. Please try again.", ex);
            }
        });

        return tenant!;
    }

    private async Task<VerificationResult> VerifyProvisionedStateAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var tenantSnapshot = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.AdminEmail })
            .FirstOrDefaultAsync(ct);

        if (tenantSnapshot is null)
        {
            return VerificationResult.Fail("Tenant record was not persisted. Please contact support.");
        }

        if (string.IsNullOrWhiteSpace(tenantSnapshot.AdminEmail))
        {
            return VerificationResult.Fail("Tenant contact information is missing. Please update the admin email before continuing.");
        }

        var membershipExists = await db.UserTenantMemberships
            .AsNoTracking()
            .AnyAsync(m => m.UserAccountId == userId && m.TenantId == tenantId && m.Status == TenantMembershipStatus.Active, ct);

        if (!membershipExists)
        {
            return VerificationResult.Fail("Your administrator membership could not be confirmed. Please contact support.");
        }

        var adminRoles = await db.UserRealmRoleAssignments
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.IsActive)
            .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Where(x => x.r.TenantId == tenantId && (x.r.Name == "tenant-admin" || x.r.Name == "admin"))
            .Select(x => x.r.Name)
            .ToListAsync(ct);

        if (!adminRoles.Contains("tenant-admin") || !adminRoles.Contains("admin"))
        {
            return VerificationResult.Fail("Administrator roles are incomplete. Please contact support before using this tenant.");
        }

        return VerificationResult.Success();
    }

    private async Task<ResolvedEmail?> ResolveCreatorEmailAsync(User creatorUser, CancellationToken ct = default)
    {
        var candidates = new List<ResolvedEmailCandidate>();

        // User profile email (preferred if verified)
        if (!string.IsNullOrWhiteSpace(creatorUser.Email))
        {
            candidates.Add(new ResolvedEmailCandidate(creatorUser.Email, creatorUser.EmailVerified));
        }

        // Claims email (fall back)
        var claimEmail = User.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(claimEmail))
        {
            candidates.Add(new ResolvedEmailCandidate(claimEmail, false));
        }

        // Decoupled account email (cross-tenant)
        var account = await db.UserAccounts
            .AsNoTracking()
            .Where(a => a.Id == creatorUser.Id)
            .Select(a => new { a.Email, a.EmailVerified })
            .FirstOrDefaultAsync(ct);

        if (account is not null && !string.IsNullOrWhiteSpace(account.Email))
        {
            candidates.Add(new ResolvedEmailCandidate(account.Email!, account.EmailVerified));
        }

        // Verified alternative email if present
        var alternativeEmail = await db.UserAlternativeEmails
            .AsNoTracking()
            .Where(a => a.UserId == creatorUser.Id && a.IsVerified)
            .Select(a => a.Email)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(alternativeEmail))
        {
            candidates.Add(new ResolvedEmailCandidate(alternativeEmail!, true));
        }

        ResolvedEmail? unverifiedFallback = null;

        foreach (var candidate in candidates)
        {
            if (!TryFormatEmail(candidate.Email, out var formatted))
            {
                continue;
            }

            if (candidate.IsVerified)
            {
                return new ResolvedEmail(formatted, true);
            }

            unverifiedFallback ??= new ResolvedEmail(formatted, false);
        }

        return unverifiedFallback;
    }

    private sealed class TenantProvisioningException : Exception
    {
        public TenantProvisioningException(string message) : base(message)
        {
        }

        public TenantProvisioningException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    private readonly record struct VerificationResult(bool Succeeded, string? ErrorMessage)
    {
        public static VerificationResult Success() => new(true, null);
        public static VerificationResult Fail(string message) => new(false, message);
    }

    private static bool TryFormatEmail(string? value, out string formatted)
    {
        formatted = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            formatted = EmailNormalizer.FormatForStorage(value, required: true, out _) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(formatted);
        }
        catch (ValidationException)
        {
            return false;
        }
    }

    private readonly record struct ResolvedEmail(string Email, bool IsVerified);

    private readonly record struct ResolvedEmailCandidate(string Email, bool IsVerified);

    private Guid? GetCurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private void CaptureCurrentUserDisplay()
    {
        CurrentUserDisplay = User.FindFirstValue(ClaimTypes.Email)
            ?? User.Identity?.Name
            ?? "current user";
    }

    // Source generator for regex
    [GeneratedRegex(@"^[a-z0-9\-]+$")]
    private static partial Regex SlugPattern();
}
