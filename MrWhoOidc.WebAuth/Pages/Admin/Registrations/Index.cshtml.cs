using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Registrations;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public IReadOnlyList<ItemVm> Items { get; private set; } = Array.Empty<ItemVm>();

    public record ItemVm(Guid Id, string Email, string? FirstName, string? LastName, string? ClientDisplay, string State, string CreatedAtLocal, string? Decision);

    public async Task OnGetAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Items = Array.Empty<ItemVm>();
            return;
        }

        // Build query scoped to current tenant
        var q = db.Set<Registration>().AsNoTracking()
            .Where(r => r.TenantId == currentTenantId.Value);

        var regs = await q.OrderByDescending(r => r.CreatedAt).ToListAsync();

        var clientIds = regs.Where(r => r.ClientId.HasValue).Select(r => r.ClientId!.Value).Distinct().ToArray();
        var clientMap = await db.Clients.AsNoTracking()
            .Where(c => clientIds.Contains(c.Id) && c.TenantId == currentTenantId.Value)
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
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Registrations");
        }

        var reg = await db.Set<Registration>().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value);
        if (reg is null) return TenantAwareRedirect("/Admin/Registrations");
        if (!string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase)) return TenantAwareRedirect("/Admin/Registrations");

        // Normalize email and prevent duplicates
        var normalized = reg.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(reg.Email) ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            reg.State = "rejected";
            reg.RejectedAt = DateTimeOffset.UtcNow;
            reg.RejectedByUserId = GetCurrentUserId();
            await db.SaveChangesAsync();
            TempData["Error"] = "Registration rejected because the email is invalid.";
            return TenantAwareRedirect("/Admin/Registrations");
        }

        string emailForUser;
        try
        {
            emailForUser = EmailNormalizer.FormatForStorage(reg.Email, required: true, out var normalizedFromFormat)
                ?? throw new ValidationException("Email is required.");
            normalized = normalizedFromFormat ?? normalized;
        }
        catch (ValidationException ex)
        {
            reg.State = "rejected";
            reg.RejectedAt = DateTimeOffset.UtcNow;
            reg.RejectedByUserId = GetCurrentUserId();
            await db.SaveChangesAsync();
            TempData["Error"] = ex.Message;
            return TenantAwareRedirect("/Admin/Registrations");
        }

        var existing = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized);
        if (existing is not null)
        {
            reg.State = "rejected";
            reg.RejectedAt = DateTimeOffset.UtcNow;
            reg.RejectedByUserId = GetCurrentUserId();
            await db.SaveChangesAsync();
            TempData["Error"] = "Registration rejected because a user with this email already exists.";
            return TenantAwareRedirect("/Admin/Registrations");
        }

        // Create user
        var user = new User
        {
            TenantId = currentTenantId.Value,
            Username = normalized,
            Email = emailForUser,
            NormalizedEmail = normalized,
            EmailVerified = false,
            Name = string.Join(' ', new[] { reg.FirstName, reg.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)))
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Optional assign to client
        if (reg.ClientId is Guid clientId)
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == currentTenantId.Value);
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
        return TenantAwareRedirect($"/admin/users/edit/{user.Id}");
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Registrations");
        }

        var reg = await db.Set<Registration>().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value);
        if (reg is null) return TenantAwareRedirect("/Admin/Registrations");
        if (!string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase)) return TenantAwareRedirect("/Admin/Registrations");

        reg.State = "rejected";
        reg.RejectedAt = DateTimeOffset.UtcNow;
        reg.RejectedByUserId = GetCurrentUserId();
        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/Registrations");
    }

    Guid? GetCurrentUserId()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
