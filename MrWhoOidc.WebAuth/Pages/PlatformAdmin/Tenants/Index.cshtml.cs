using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class IndexModel(
    AuthDbContext db,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    public sealed record TenantRow(
        Guid Id,
        string Slug,
        string Name,
        string? Description,
        string IssuerUri,
        TenantStatus Status,
        int UserCount,
        int ClientCount,
        int MaxUsers,
        int MaxClients,
        DateTimeOffset CreatedAt);

    public IReadOnlyList<TenantRow> TenantRows { get; private set; } = Array.Empty<TenantRow>();

    public string CurrentMode => multiTenancyOptions.Enabled ? "MultiTenant" : "SingleTenant";

    public string? DefaultTenantSlug => multiTenancyOptions.DefaultTenantSlug;

    public async Task OnGetAsync()
    {
        // Redirect to dashboard if multi-tenancy is disabled
        if (!multiTenancyOptions.Enabled)
        {
            Response.Redirect("/PlatformAdmin/Index");
            return;
        }

        // Load all tenants with counts
        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id,
                t.Slug,
                t.Name,
                t.Description,
                t.IssuerUri,
                t.Status,
                t.MaxUsers,
                t.MaxClients,
                t.CreatedAt,
                UserCount = db.Users.Count(u => u.TenantId == t.Id),
                ClientCount = db.Clients.Count(c => c.TenantId == t.Id)
            })
            .ToListAsync();

        TenantRows = tenants.Select(t => new TenantRow(
            t.Id,
            t.Slug,
            t.Name,
            t.Description,
            t.IssuerUri,
            t.Status,
            t.UserCount,
            t.ClientCount,
            t.MaxUsers,
            t.MaxClients,
            t.CreatedAt
        )).ToList();
    }
}
