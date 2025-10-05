using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Net.Http.Json;

namespace MrWhoOidc.WebAuth.Pages.Admin.Backchannel;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(AuthDbContext db, MrWhoOidc.WebAuth.Background.BackchannelRuntimeState state) : PageModel
{
    public sealed record Item(Guid Id, string ClientId, string TargetUri, string Status, int AttemptCount, int MaxAttempts, int? LastHttpStatus, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt);
    public sealed record CircuitItem(string ClientId, int Failures, DateTimeOffset? OpenUntil);

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    public long Backlog { get; private set; }
    public bool Enabled { get; private set; }
    public List<Item> Items { get; private set; } = new();
    public List<CircuitItem> OpenCircuits { get; private set; } = new();

    public async Task OnGetAsync()
    {
        // Query API for items and backlog
        var q = db.BackchannelLogoutNotifications.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Status)) q = q.Where(n => n.Status == Status);
        Items = await q.OrderByDescending(n => n.CreatedAt)
            .Take(200)
            .Select(n => new Item(n.Id, n.ClientId, n.TargetUri, n.Status, n.AttemptCount, n.MaxAttempts, n.LastHttpStatus, n.LastError, n.CreatedAt, n.LastAttemptAt, n.NextAttemptAt))
            .ToListAsync();
        var now = DateTimeOffset.UtcNow;
        Backlog = await db.BackchannelLogoutNotifications.LongCountAsync(n => n.Status == "pending" && (n.NextAttemptAt == null || n.NextAttemptAt <= now));

        // Read runtime state for circuits and flag
        Enabled = state.EmissionEnabled;
        OpenCircuits = state.Circuits
            .Where(kv => kv.Value.OpenUntil is not null && kv.Value.OpenUntil > DateTimeOffset.UtcNow)
            .Select(kv => new CircuitItem(kv.Key, kv.Value.Failures, kv.Value.OpenUntil))
            .OrderByDescending(c => c.Failures)
            .Take(50)
            .ToList();
    }

    public async Task<IActionResult> OnPostRetryAsync([FromForm] Guid id)
    {
        var entity = await db.BackchannelLogoutNotifications.FirstOrDefaultAsync(n => n.Id == id);
        if (entity is not null)
        {
            entity.Status = "pending";
            entity.NextAttemptAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { status = Status });
    }

    // No DTOs needed; reading state directly
}
