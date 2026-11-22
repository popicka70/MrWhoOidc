using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Impersonation;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.UserImpersonation;

/// <summary>Audit history for real user impersonation sessions.</summary>
[Authorize(Policy = "platform-admin")]
public class HistoryModel(
    AuthDbContext db,
    IUserImpersonationStore store,
    IUserImpersonationService impersonationService,
    IOptions<AuthOptions> authOptions) : PageModel
{
    private const int PageSize = 50;

    public bool FeatureEnabled { get; private set; }
    public List<UserImpersonationSession> Sessions { get; private set; } = new();
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; }
    public int TotalCount { get; private set; }
    public int ActiveCount { get; private set; }
    public string? ActorUsername { get; private set; }
    public string? TargetUsername { get; private set; }
    public string? TenantSlug { get; private set; }
    public DateTime? StartDate { get; private set; }
    public DateTime? EndDate { get; private set; }

    public async Task OnGetAsync(
        int page = 1,
        string? actorUsername = null,
        string? targetUsername = null,
        string? tenantSlug = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        FeatureEnabled = authOptions.Value.EnableUserImpersonation;
        if (!FeatureEnabled)
        {
            return;
        }

        CurrentPage = Math.Max(1, page);
        ActorUsername = actorUsername;
        TargetUsername = targetUsername;
        TenantSlug = tenantSlug;
        StartDate = startDate;
        EndDate = endDate;

        var from = startDate is { } s ? new DateTimeOffset(s.Date.ToUniversalTime()) : (DateTimeOffset?)null;
        var to = endDate is { } e ? new DateTimeOffset(e.Date.AddDays(1).ToUniversalTime()) : (DateTimeOffset?)null;

        TotalCount = await store.CountAsync(
            actorUsername, targetUsername, tenantSlug, from, to, HttpContext.RequestAborted);
        TotalPages = (int)Math.Ceiling(TotalCount / (double)PageSize);
        var now = DateTimeOffset.UtcNow;
        ActiveCount = await db.UserImpersonationSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(s => s.Status == UserImpersonationStatus.Active && s.ExpiresAt > now,
                HttpContext.RequestAborted);

        Sessions = await store.ListAsync(
            actorUsername, targetUsername, tenantSlug, from, to,
            (CurrentPage - 1) * PageSize, PageSize, HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostRevokeAsync(
        Guid sessionId,
        string reason,
        int page = 1,
        string? actorUsername = null,
        string? targetUsername = null,
        string? tenantSlug = null,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "A revocation reason is required.";
            return RedirectToPage(new { page, actorUsername, targetUsername, tenantSlug, startDate, endDate });
        }

        var revoked = await impersonationService.RevokeAsync(User, sessionId, reason);
        TempData[revoked ? "Success" : "Error"] = revoked
            ? "The impersonation session was revoked and its target OP session ended."
            : "The impersonation session could not be revoked.";
        return RedirectToPage(new { page, actorUsername, targetUsername, tenantSlug, startDate, endDate });
    }

    public string Duration(UserImpersonationSession session)
    {
        var end = session.EndedAt ?? session.RevokedAt ?? DateTimeOffset.UtcNow;
        var duration = end - session.CreatedAt;
        return duration.TotalMinutes < 1
            ? "< 1 min"
            : duration.TotalHours < 1
                ? $"{(int)duration.TotalMinutes} min"
                : $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }
}
