using System.Text;
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
    /// Current page number (1-based). Binds from the "Page" query key because the pagination links
    /// (asp-route-Page) generate <c>?Page=N</c>.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "Page")]
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
    /// Filter by actor (substring match on who performed the operation).
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? ActorFilter { get; set; }

    /// <summary>
    /// Earliest entry date (inclusive), as a UTC date.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    /// <summary>
    /// Latest entry date (inclusive), as a UTC date.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? ToDate { get; set; }

    /// <summary>
    /// When set to "csv", OnGet returns the filtered audit entries as a CSV download instead of the page.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Export { get; set; }

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

        // Apply actor filter (substring match on the performer)
        if (!string.IsNullOrEmpty(ActorFilter))
        {
            var actor = ActorFilter.Trim();
            query = query.Where(a => a.PerformedBy.Contains(actor));
        }

        // Apply date-range filter (UTC dates: FromDate is the start of the day, ToDate the end of it)
        if (FromDate.HasValue)
        {
            var from = new DateTimeOffset(FromDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.Timestamp >= from);
        }
        if (ToDate.HasValue)
        {
            var toExclusive = new DateTimeOffset(
                ToDate.Value.ToDateTime(TimeOnly.MinValue).AddDays(1), TimeSpan.Zero);
            query = query.Where(a => a.Timestamp < toExclusive);
        }

        // CSV export: the current filters select the exported set
        if (string.Equals(Export, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var entries = await query
                .OrderByDescending(a => a.Timestamp)
                .Take(MaxExportRows)
                .ToListAsync();
            return File(
                Encoding.UTF8.GetBytes(BuildCsv(entries)),
                "text/csv; charset=utf-8",
                $"configuration-audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
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

    /// <summary>Upper bound on the number of rows a CSV export may contain.</summary>
    private const int MaxExportRows = 5000;

    private static string BuildCsv(List<ConfigurationAuditLog> entries)
    {
        static string Escape(string? value)
        {
            var text = value ?? string.Empty;
            return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            "Timestamp,Operation,EntityType,EntityIdentifier,ExportMode,Result,"
            + "EntitiesCreated,EntitiesUpdated,EntitiesSkipped,PerformedBy,IpAddress,ErrorDetails");
        foreach (var entry in entries)
        {
            sb.AppendLine(string.Join(",",
                Escape(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'")),
                Escape(entry.Operation),
                Escape(entry.EntityType),
                Escape(entry.EntityIdentifier),
                Escape(entry.ExportMode),
                Escape(entry.Result),
                Escape(entry.EntitiesCreated?.ToString()),
                Escape(entry.EntitiesUpdated?.ToString()),
                Escape(entry.EntitiesSkipped?.ToString()),
                Escape(entry.PerformedBy),
                Escape(entry.IpAddress),
                Escape(entry.ErrorDetails)));
        }
        return sb.ToString();
    }
}
