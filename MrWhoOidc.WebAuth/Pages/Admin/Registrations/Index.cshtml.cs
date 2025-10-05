using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Registrations;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : PageModel
{
    public IReadOnlyList<ItemVm> Items { get; private set; } = Array.Empty<ItemVm>();
    public List<SelectListItem> TenantOptions { get; private set; } = new();
    public bool IsPlatformAdmin { get; private set; }
    
    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    public record ItemVm(Guid Id, string Email, string? FirstName, string? LastName, string? ClientDisplay, string State, string CreatedAtLocal, string? Decision);

    public async Task OnGetAsync()
    {
        // Check if user is platform admin
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;
        
        // Load tenant options for filter (platform admins only)
        if (IsPlatformAdmin)
        {
            var tenants = await db.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active)
                .OrderBy(t => t.Name)
                .ToListAsync();
            TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
            TenantOptions.Insert(0, new SelectListItem("All Tenants", ""));
        }
        
        // Build query with automatic tenant scoping
        var q = db.Set<Registration>().AsNoTracking();
        
        if (IsPlatformAdmin)
        {
            // Platform admins can optionally filter by tenant
            if (TenantId.HasValue)
            {
                q = q.Where(r => r.TenantId == TenantId.Value);
            }
        }
        else
        {
            // Regular tenant admins only see their tenant
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (currentTenantId.HasValue)
            {
                q = q.Where(r => r.TenantId == currentTenantId.Value);
            }
            else
            {
                // No tenant context, return empty
                Items = Array.Empty<ItemVm>();
                return;
            }
        }
        
        var regs = await q.OrderByDescending(r => r.CreatedAt).ToListAsync();

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

        // Normalize email and prevent duplicates
        var normalized = reg.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(reg.Email) ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            reg.State = "rejected";
            reg.RejectedAt = DateTimeOffset.UtcNow;
            reg.RejectedByUserId = GetCurrentUserId();
            await db.SaveChangesAsync();
            TempData["Error"] = "Registration rejected because the email is invalid.";
            return RedirectToPage();
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
            return RedirectToPage();
        }

        var existing = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized);
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
            Username = normalized,
            Email = emailForUser,
            EmailVerified = false,
            Name = string.Join(' ', new[] { reg.FirstName, reg.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))),
            HashAlgorithm = "argon2id",
            PasswordHash = string.IsNullOrEmpty(reg.PasswordHash) ? string.Empty : reg.PasswordHash
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
