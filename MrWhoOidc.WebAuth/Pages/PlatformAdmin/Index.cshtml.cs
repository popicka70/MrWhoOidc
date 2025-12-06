using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class IndexModel : PageModel
{
    private readonly AuthDbContext _db;
    private readonly ITenantSeedingService _seedingService;
    private readonly ILogger<IndexModel> _logger;
    private readonly IMultiTenancyOptions _multiTenancyOptions;

    public IndexModel(
        AuthDbContext db,
        ITenantSeedingService seedingService,
        ILogger<IndexModel> logger,
        IMultiTenancyOptions multiTenancyOptions)
    {
        _db = db;
        _seedingService = seedingService;
        _logger = logger;
        _multiTenancyOptions = multiTenancyOptions;
    }

    // Stats
    public int TotalTenants { get; set; }
    public int ActiveTenants { get; set; }
    public int TotalUsers { get; set; }
    public int TotalClients { get; set; }

    // Multi-tenancy status
    public bool IsMultiTenantMode => _multiTenancyOptions.Enabled;

    // Recent tenants
    public List<TenantSummary> RecentTenants { get; set; } = new();

    public async Task OnGetAsync()
    {
        // Get stats
        TotalTenants = await _db.Tenants.CountAsync();
        ActiveTenants = await _db.Tenants.CountAsync(t => t.Status == TenantStatus.Active);
        TotalUsers = await _db.Users.CountAsync();
        TotalClients = await _db.Clients.CountAsync();

        // Get recent tenants with counts
        RecentTenants = await _db.Tenants
            .OrderByDescending(t => t.CreatedAt)
            .Take(10)
            .Select(t => new TenantSummary
            {
                Id = t.Id,
                Slug = t.Slug,
                Name = t.Name,
                Status = t.Status,
                CreatedAt = t.CreatedAt,
                UserCount = _db.Users.Count(u => u.TenantId == t.Id),
                ClientCount = _db.Clients.Count(c => c.TenantId == t.Id)
            })
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostSeedTenantAsync(string tenantSlug, string tenantName, string? adminEmail, string? adminPassword)
    {
        // Guard: Seeding is only allowed in multi-tenant mode
        if (!_multiTenancyOptions.Enabled)
        {
            _logger.LogWarning("Attempt to seed tenant in single-tenant mode by user {User}", User.Identity?.Name);
            throw new InvalidOperationException("Tenant seeding is only available in multi-tenant mode.");
        }

        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            TempData["ErrorMessage"] = "Tenant slug is required.";
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(tenantName))
        {
            TempData["ErrorMessage"] = "Tenant name is required.";
            return RedirectToPage();
        }

        _logger.LogInformation("Platform admin {User} initiating tenant seeding for slug: {Slug}", User.Identity?.Name, tenantSlug);

        var result = await _seedingService.SeedSampleTenantAsync(tenantSlug, tenantName, adminEmail, adminPassword);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("Tenant seeding failed for slug {Slug}: {Error}", tenantSlug, result.ErrorMessage);
            TempData["ErrorMessage"] = $"Failed to seed tenant: {result.ErrorMessage}";
            return RedirectToPage();
        }

        _logger.LogInformation("Successfully seeded tenant {Slug} (ID: {TenantId})", result.TenantSlug, result.TenantId);

        var loginUrl = $"https://localhost:8443/t/{result.TenantSlug}/login";
        var adminUrl = $"https://localhost:8443/t/{result.TenantSlug}/admin/users";

        TempData["SuccessMessage"] = $"✅ Tenant '{result.TenantName}' created successfully! " +
            $"Login at: {loginUrl} | " +
            $"Email: {result.AdminEmail} | " +
            $"Password: {result.AdminPassword}";

        return RedirectToPage();
    }

    public class TenantSummary
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TenantStatus Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int UserCount { get; set; }
        public int ClientCount { get; set; }
    }
}
