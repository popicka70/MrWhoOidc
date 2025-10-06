using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.ImpersonationHistory;

/// <summary>
/// Displays audit history of all impersonation sessions for compliance and security monitoring.
/// Only accessible to platform admins.
/// </summary>
[Authorize(Policy = "platform-admin")]
public class IndexModel(AuthDbContext db) : PageModel
{
    public List<ImpersonationAuditLog> Logs { get; set; } = new();
    public FilterModel Filter { get; set; } = new();
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

        // Build query with filters
        var query = db.ImpersonationAuditLogs.AsQueryable();

        if (Filter.StartDate.HasValue)
        {
            query = query.Where(l => l.Timestamp >= Filter.StartDate.Value);
        }

        if (Filter.EndDate.HasValue)
        {
            var endOfDay = Filter.EndDate.Value.AddDays(1);
            query = query.Where(l => l.Timestamp < endOfDay);
        }

        if (!string.IsNullOrWhiteSpace(Filter.AdminUsername))
        {
            query = query.Where(l => l.PlatformAdminUsername.Contains(Filter.AdminUsername));
        }

        if (!string.IsNullOrWhiteSpace(Filter.TenantSlug))
        {
            query = query.Where(l => l.TenantSlug.Contains(Filter.TenantSlug));
        }

        // Get total count
        TotalCount = await query.CountAsync();
        TotalPages = (int)Math.Ceiling(TotalCount / (double)PageSize);

        // Get paginated logs
        Logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        // Calculate statistics
        TotalSessions = await db.ImpersonationAuditLogs
            .Where(l => l.Action == ImpersonationAction.Start)
            .CountAsync();

        // Active sessions = Start logs without corresponding Stop logs
        var startLogIds = await db.ImpersonationAuditLogs
            .Where(l => l.Action == ImpersonationAction.Start)
            .Select(l => l.Id)
            .ToListAsync();

        var stopLogStartIds = await db.ImpersonationAuditLogs
            .Where(l => l.Action == ImpersonationAction.Stop && l.StartLogId != null)
            .Select(l => l.StartLogId!.Value)
            .ToListAsync();

        ActiveSessions = startLogIds.Except(stopLogStartIds).Count();
    }

    public async Task<IActionResult> OnPostExportCsvAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? adminUsername = null,
        string? tenantSlug = null)
    {
        // Build query with same filters
        var query = db.ImpersonationAuditLogs.AsQueryable();

        if (startDate.HasValue)
        {
            query = query.Where(l => l.Timestamp >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            var endOfDay = endDate.Value.AddDays(1);
            query = query.Where(l => l.Timestamp < endOfDay);
        }

        if (!string.IsNullOrWhiteSpace(adminUsername))
        {
            query = query.Where(l => l.PlatformAdminUsername.Contains(adminUsername));
        }

        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            query = query.Where(l => l.TenantSlug.Contains(tenantSlug));
        }

        // Get all matching logs (no pagination for export)
        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .ToListAsync();

        // Generate CSV
        var csv = new StringBuilder();
        csv.AppendLine("Timestamp,Action,Admin Username,Admin User ID,Tenant Name,Tenant Slug,Tenant ID,Duration (seconds),IP Address,User Agent");

        foreach (var log in logs)
        {
            csv.AppendLine($"\"{log.Timestamp:yyyy-MM-dd HH:mm:ss}\"," +
                          $"\"{log.Action}\"," +
                          $"\"{EscapeCsv(log.PlatformAdminUsername)}\"," +
                          $"\"{log.PlatformAdminUserId}\"," +
                          $"\"{EscapeCsv(log.TenantName)}\"," +
                          $"\"{log.TenantSlug}\"," +
                          $"\"{log.TenantId}\"," +
                          $"\"{log.Duration?.TotalSeconds.ToString() ?? ""}\"," +
                          $"\"{log.IpAddress ?? ""}\"," +
                          $"\"{EscapeCsv(log.UserAgent ?? "")}\"");
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var fileName = $"impersonation-history-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";

        return File(bytes, "text/csv", fileName);
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Escape quotes by doubling them
        return value.Replace("\"", "\"\"");
    }
}

public class FilterModel
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? AdminUsername { get; set; }
    public string? TenantSlug { get; set; }
}
