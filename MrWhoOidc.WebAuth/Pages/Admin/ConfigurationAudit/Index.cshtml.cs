using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.ConfigurationAudit;

/// <summary>
/// Page model for viewing configuration export/import audit logs.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class IndexModel : TenantAwarePageModel
{
    private readonly AuthDbContext _dbContext;

    public IndexModel(
        AuthDbContext dbContext,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions)
        : base(tenantAccessor, multiTenancyOptions)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Audit log entries for the current page.
    /// </summary>
    public List<ConfigurationAuditLog> AuditLogs { get; set; } = [];

    /// <summary>
    /// Current page number (1-based).
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public int CurrentPage { get; set; } = 1;

    /// <summary>
    /// Number of items per page.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Filter by operation type.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? OperationFilter { get; set; }

    /// <summary>
    /// Filter by entity type.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? EntityTypeFilter { get; set; }

    /// <summary>
    /// Total number of matching records.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Total number of pages.
    /// </summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

    /// <summary>
    /// Selected audit log for detail view.
    /// </summary>
    public ConfigurationAuditLog? SelectedAuditLog { get; set; }

    /// <summary>
    /// ID of the audit log to view details for.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public Guid? DetailId { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId;

        var query = _dbContext.ConfigurationAuditLogs.AsNoTracking();

        // Filter by tenant
        if (tenantId.HasValue)
        {
            query = query.Where(a => a.TenantId == tenantId.Value);
        }

        // Apply operation filter
        if (!string.IsNullOrEmpty(OperationFilter))
        {
            query = query.Where(a => a.Operation == OperationFilter);
        }

        // Apply entity type filter
        if (!string.IsNullOrEmpty(EntityTypeFilter))
        {
            query = query.Where(a => a.EntityType == EntityTypeFilter);
        }

        // Ensure valid pagination
        CurrentPage = Math.Max(1, CurrentPage);
        PageSize = Math.Clamp(PageSize, 1, 100);

        TotalCount = await query.CountAsync();
        AuditLogs = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        // Load detail if requested
        if (DetailId.HasValue)
        {
            SelectedAuditLog = await _dbContext.ConfigurationAuditLogs
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == DetailId.Value && a.TenantId == tenantId);
        }

        return Page();
    }
}
