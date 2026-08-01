using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.SupportAccessHistory;

/// <summary>
/// Displays history of all support access sessions for compliance and security monitoring.
/// Queries durable TenantSupportAccessSessions instead of legacy audit logs.
/// Only accessible to platform admins.
/// </summary>
[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class IndexModel(
    AuthDbContext db,
    ITenantSupportAccessService supportAccessService) : PageModel
{
    public List<SessionDisplayRow> Sessions { get; set; } = new();
    public FilterModel Filter { get; set; } = new();
    public Dictionary<Guid, string> UserNames { get; set; } = new();
    public Dictionary<Guid, string> TenantNames { get; set; } = new();
    public Dictionary<Guid, string> TenantSlugs { get; set; } = new();
    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }
    public int TotalSessions { get; set; }
    public int ActiveSessions { get; set; }

    private const int PageSize = 50;

    public async Task OnGetAsync(
        int page = 1,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? adminUsername = null,
        string? tenantSlug = null)
    {
        CurrentPage = Math.Max(1, page);
        Filter = new FilterModel
        {
            StartDate = startDate,
            EndDate = endDate,
            AdminUsername = adminUsername,
            TenantSlug = tenantSlug
        };

        // Resolve user names and tenant names for display
        // Build lookup maps from the database
        var userLookup = await db.Users
            .AsNoTracking()
            .Select(u => new { u.Id, u.Name })
            .ToListAsync();

        UserNames = userLookup.ToDictionary(u => u.Id, u => u.Name);

        var tenantLookup = await db.Tenants
            .AsNoTracking()
            .Select(t => new { t.Id, t.Name, t.Slug })
            .ToListAsync();

        TenantNames = tenantLookup.ToDictionary(t => t.Id, t => t.Name);
        TenantSlugs = tenantLookup.ToDictionary(t => t.Id, t => t.Slug);

        // Query TenantSupportAccessSessions directly
        var query = db.TenantSupportAccessSessions.AsQueryable();

        if (Filter.StartDate.HasValue)
        {
            var startDt = new DateTimeOffset(Filter.StartDate.Value);
            query = query.Where(s => s.CreatedAt >= startDt);
        }

        if (Filter.EndDate.HasValue)
        {
            var endOfDay = Filter.EndDate.Value.AddDays(1);
            var endDt = new DateTimeOffset(endOfDay);
            query = query.Where(s => s.CreatedAt < endDt);
        }

        if (!string.IsNullOrWhiteSpace(Filter.AdminUsername))
        {
            query = query.Where(s => db.Users.Any(u => u.Id == s.PlatformAdminUserAccountId && u.Name.Contains(Filter.AdminUsername)));
        }

        if (!string.IsNullOrWhiteSpace(Filter.TenantSlug))
        {
            query = query.Where(s => db.Tenants.Any(t => t.Id == s.TenantId && t.Slug.Contains(Filter.TenantSlug)));
        }

        // Get total count
        TotalCount = await query.CountAsync();
        TotalPages = (int)Math.Ceiling(TotalCount / (double)PageSize);

        // Get paginated sessions
        Sessions = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
        .Select(s => new SessionDisplayRow
        {
            SessionId = s.Id,
            PlatformAdminUserAccountId = s.PlatformAdminUserAccountId,
            TenantId = s.TenantId,
            Reason = s.Reason,
            TicketReference = s.TicketReference,
            CreatedAt = s.CreatedAt,
            ExpiresAt = s.ExpiresAt,
            Status = s.Status,
            EndedAt = s.EndedAt,
            AdminName = UserNames[s.PlatformAdminUserAccountId] ?? "Unknown",
            TenantName = TenantNames[s.TenantId] ?? "Unknown",
            TenantSlug = TenantSlugs[s.TenantId] ?? "unknown"
        })
            .ToListAsync();

        // Calculate statistics
        TotalSessions = await db.TenantSupportAccessSessions.CountAsync();

        // Active sessions: those with Status == Active and not expired
        ActiveSessions = await db.TenantSupportAccessSessions
            .Where(s => s.Status == SupportAccessStatus.Active && s.ExpiresAt > DateTimeOffset.UtcNow)
            .CountAsync();
    }

    public async Task<IActionResult> OnPostExportCsvAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? adminUsername = null,
        string? tenantSlug = null)
    {
        // Build query with same filters
        var query = db.TenantSupportAccessSessions.AsQueryable();

        if (startDate.HasValue)
        {
            var startDt = new DateTimeOffset(startDate.Value);
            query = query.Where(s => s.CreatedAt >= startDt);
        }

        if (endDate.HasValue)
        {
            var endOfDay = endDate.Value.AddDays(1);
            var endDt = new DateTimeOffset(endOfDay);
            query = query.Where(s => s.CreatedAt < endDt);
        }

        if (!string.IsNullOrWhiteSpace(adminUsername))
        {
            query = query.Where(s => db.Users.Any(u => u.Id == s.PlatformAdminUserAccountId && u.Name.Contains(adminUsername)));
        }

        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            query = query.Where(s => db.Tenants.Any(t => t.Id == s.TenantId && t.Slug.Contains(tenantSlug)));
        }

        // Get all matching sessions (no pagination for export)
        var sessions = await query
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        // Generate CSV
        var csv = new StringBuilder();
        csv.AppendLine("Session ID,Admin User ID,Tenant ID,Reason,Ticket Reference,Started At,Expires At,Status,Ended At");

        foreach (var s in sessions)
        {
            csv.AppendLine($"\"{s.Id}\"," +
                           $"\"{s.PlatformAdminUserAccountId}\"," +
                           $"\"{s.TenantId}\"," +
                           $"\"{EscapeCsv(s.Reason)}\"," +
                           $"\"{EscapeCsv(s.TicketReference ?? "")}\"," +
                           $"\"{s.CreatedAt:yyyy-MM-dd HH:mm:ss}\"," +
                           $"\"{s.ExpiresAt:yyyy-MM-dd HH:mm:ss}\"," +
                           $"\"{s.Status}\"," +
                           $"\"{s.EndedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""}\"");
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var fileName = $"support-access-history-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";

        return File(bytes, "text/csv", fileName);
    }

    public async Task<IActionResult> OnPostRevokeAsync(
        Guid sessionId,
        string reason,
        int page = 1,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? adminUsername = null,
        string? tenantSlug = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "A revocation reason is required.";
        }
        else if (await supportAccessService.RevokeSupportAccessAsync(User, sessionId, reason, HttpContext))
        {
            TempData["Success"] = "Support access session revoked.";
        }
        else
        {
            TempData["Error"] = "The support access session was not active or could not be revoked.";
        }

        return RedirectToPage("Index", new
        {
            page,
            startDate,
            endDate,
            adminUsername,
            tenantSlug
        });
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Escape quotes by doubling them
        return value.Replace("\"", "\"\"");
    }
}

/// <summary>
/// Display row for a support access session in the history view.
/// </summary>
public class SessionDisplayRow
{
    public Guid SessionId { get; set; }
    public Guid PlatformAdminUserAccountId { get; set; }
    public Guid TenantId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? TicketReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public SupportAccessStatus Status { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string AdminName { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
}

public class FilterModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? AdminUsername { get; set; }
    public string? TenantSlug { get; set; }
}
