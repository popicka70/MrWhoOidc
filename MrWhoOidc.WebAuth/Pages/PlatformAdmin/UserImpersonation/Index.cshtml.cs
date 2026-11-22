using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.UserImpersonation;

/// <summary>
/// Platform-admin control page for real user impersonation.
/// </summary>
[Authorize(Policy = "platform-admin")]
public class IndexModel(
    AuthDbContext db,
    IUserImpersonationService impersonationService,
    ITenantSupportAccessService supportAccessService,
    IMultiTenancyOptions multiTenancyOptions,
    IOptions<AuthOptions> authOptions) : PageModel
{
    public bool FeatureEnabled { get; private set; }
    public List<TenantOption> Tenants { get; private set; } = new();
    public Guid? SelectedTenantId { get; private set; }
    public string SelectedTenantName { get; private set; } = string.Empty;
    public string SelectedTenantSlug { get; private set; } = string.Empty;
    public string? Search { get; private set; }
    public string? ReturnUrl { get; private set; }
    public List<UserOption> Users { get; private set; } = new();
    public UserImpersonationInfo? CurrentImpersonation { get; private set; }
    public TenantSupportAccessInfo? CurrentSupportAccess { get; private set; }
    public bool MultiTenant => multiTenancyOptions.Enabled;

    public async Task OnGetAsync(Guid? tenantId = null, string? q = null, string? returnUrl = null)
    {
        FeatureEnabled = authOptions.Value.EnableUserImpersonation;
        if (!FeatureEnabled)
        {
            return;
        }

        ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl) ? null : returnUrl;

        CurrentImpersonation = await impersonationService.GetInfoAsync(HttpContext);
        CurrentSupportAccess = await supportAccessService.GetSupportAccessInfoAsync(HttpContext);

        Tenants = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderByDescending(t => t.Status == TenantStatus.Active)
            .ThenBy(t => t.Name)
            .Select(t => new TenantOption
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                IsActive = t.Status == TenantStatus.Active
            })
            .ToListAsync(HttpContext.RequestAborted);

        var candidate = tenantId
            ?? CurrentImpersonation?.TenantId
            ?? Tenants.FirstOrDefault(t => t.Slug == multiTenancyOptions.DefaultTenantSlug)?.Id
            ?? Tenants.FirstOrDefault(t => t.IsActive)?.Id;
        var selected = Tenants.FirstOrDefault(t => t.Id == candidate);
        if (selected is not null)
        {
            SelectedTenantId = selected.Id;
            SelectedTenantName = selected.Name;
            SelectedTenantSlug = selected.Slug;
        }

        Search = q?.Trim();
        if (SelectedTenantId is { } selectedId && !string.IsNullOrWhiteSpace(Search))
        {
            var term = Search;
            Users = await db.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.TenantId == selectedId
                            && (u.Username.Contains(term)
                                || (u.Email != null && u.Email.Contains(term))
                                || (u.Name != null && u.Name.Contains(term))))
                .OrderBy(u => u.Username)
                .Take(50)
                .Select(u => new UserOption
                {
                    Id = u.Id,
                    Username = u.Username,
                    Name = u.Name,
                    Email = u.Email,
                    IsActive = u.Status == UserStatus.Active
                })
                .ToListAsync(HttpContext.RequestAborted);
        }
    }

    public async Task<IActionResult> OnPostStartAsync(
        Guid targetUserId,
        string reason,
        int? expiryMinutes = null,
        string? ticketReference = null,
        Guid? tenantId = null,
        string? returnUrl = null)
    {
        var result = await impersonationService.StartAsync(
            HttpContext, User, targetUserId, reason, expiryMinutes, ticketReference);
        if (!result.Success)
        {
            TempData["Error"] = result.Error ?? "Could not start impersonation.";
            return RedirectToPage(new { tenantId });
        }

        TempData["Success"] =
            $"Now impersonating {result.TargetDisplayName ?? result.TargetUsername} in {result.TenantName}. " +
            $"The session ends at {result.ExpiresAt.ToLocalTime():HH:mm}.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return Redirect($"/t/{result.TenantSlug}/account");
    }

    public sealed class TenantOption
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    public sealed class UserOption
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string? Email { get; init; }
        public bool IsActive { get; init; }
    }
}
