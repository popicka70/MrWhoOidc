using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Registrations;

[Authorize(Policy = "admin")]
public class IndexModel(AuthDbContext db) : PageModel
{
    public IReadOnlyList<ItemVm> Items { get; private set; } = Array.Empty<ItemVm>();

    public record ItemVm(Guid Id, string Email, string? FirstName, string? LastName, string? ClientDisplay, string State, string CreatedAtLocal, string? Decision);

    public async Task OnGetAsync()
    {
        var regs = await db.Set<Registration>().AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var clientIds = regs.Where(r => r.ClientId.HasValue).Select(r => r.ClientId!.Value).Distinct().ToArray();
        var clientMap = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .Join(db.Realms.AsNoTracking(), c => c.RealmId, rl => rl.Id, (c, rl) => new { c.Id, Display = $"{c.ClientId} ({rl.Name})" })
            .ToDictionaryAsync(x => x.Id, x => x.Display);

        Items = regs.Select(r =>
        {
            clientMap.TryGetValue(r.ClientId ?? Guid.Empty, out var display);
            var created = r.CreatedAt.ToLocalTime().ToString("g");
            string? decision = r.State == "approved"
                ? (r.ApprovedAt.HasValue ? $"Approved {r.ApprovedAt.Value.ToLocalTime():g}" : "Approved")
                : r.State == "rejected"
                    ? (r.RejectedAt.HasValue ? $"Rejected {r.RejectedAt.Value.ToLocalTime():g}" : "Rejected")
                    : null;
            return new ItemVm(r.Id, r.Email, r.FirstName, r.LastName, display, r.State, created, decision);
        }).ToList();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id)
    {
        var reg = await db.Set<Registration>().FirstOrDefaultAsync(r => r.Id == id);
        if (reg is null) return RedirectToPage();
        if (!string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase)) return RedirectToPage();

        // Prevent duplicates: if user exists now, reject with warning
        var email = reg.Email.Trim().ToLowerInvariant();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (existing is not null)
        {
            reg.State = "rejected";
            reg.RejectedAt = DateTimeOffset.UtcNow;
            reg.RejectedByUserId = GetCurrentUserId();
            await db.SaveChangesAsync();
            TempData["Error"] = "Registration rejected because a user with this email already exists.";
            return RedirectToPage();
        }

        // Create user
        var user = new User
        {
            Username = email,
            Email = email,
            EmailVerified = false,
            Name = string.Join(' ', new[] { reg.FirstName, reg.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)))
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Optional assign to client
        if (reg.ClientId is Guid clientId)
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId);
            if (client is not null)
            {
                var exists = await db.UserClientAssignments.AnyAsync(a => a.UserId == user.Id && a.ClientId == client.Id && a.RealmId == client.RealmId);
                if (!exists)
                {
                    db.UserClientAssignments.Add(new UserClientAssignment { UserId = user.Id, ClientId = client.Id, RealmId = client.RealmId, IsActive = true });
                    await db.SaveChangesAsync();
                }
            }
        }

        reg.State = "approved";
        reg.ApprovedAt = DateTimeOffset.UtcNow;
        reg.ApprovedByUserId = GetCurrentUserId();
        await db.SaveChangesAsync();

        // Go to user edit
        return RedirectToPage("/Admin/Users/Edit", new { id = user.Id });
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id)
    {
        var reg = await db.Set<Registration>().FirstOrDefaultAsync(r => r.Id == id);
        if (reg is null) return RedirectToPage();
        if (!string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase)) return RedirectToPage();

        reg.State = "rejected";
        reg.RejectedAt = DateTimeOffset.UtcNow;
        reg.RejectedByUserId = GetCurrentUserId();
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    Guid? GetCurrentUserId()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
