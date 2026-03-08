using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    IPasswordHasher hasher,
    IClientIdGenerator idGen,
    ITenantAccessor tenantAccessor,
    IClientStore clientStore,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record ClientRow(Guid Id, string ClientId, string? ClientName, string RealmName, Guid TenantId, string TenantName, bool RequirePkce, bool RequireConsent, bool HasJwks, bool RequirePar);

    public IReadOnlyList<ClientRow> Clients { get; private set; } = Array.Empty<ClientRow>();

    public List<SelectListItem> RealmOptions { get; private set; } = new();

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Clients = Array.Empty<ClientRow>();
            RealmOptions = new List<SelectListItem>();
            return;
        }

        var realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == currentTenantId.Value)
            .OrderBy(r => r.Name)
            .ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();

        // Build query with tenant and realm JOINs
        var q = db.Clients.AsNoTracking()
            .Where(c => c.TenantId == currentTenantId.Value)
            .Join(db.Tenants, c => c.TenantId, t => t.Id, (c, t) => new { Client = c, Tenant = t })
            .Join(db.Realms, x => x.Client.RealmId, r => r.Id, (x, r) => new { x.Client, x.Tenant, Realm = r });

        Clients = await q
            .OrderBy(x => x.Client.ClientId)
            .Select(x => new ClientRow(
                x.Client.Id,
                x.Client.ClientId,
                x.Client.ClientName,
                x.Realm.Name,
                x.Client.TenantId,
                x.Tenant.Name,
                x.Client.RequirePkce,
                x.Client.RequireConsent,
                !string.IsNullOrEmpty(x.Client.PublicJwksJson) || !string.IsNullOrEmpty(x.Client.PublicJwksUri),
                x.Client.RequirePar
            ))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        // Unique client id
        if (await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId))
        {
            ModelState.AddModelError("Input.ClientId", "Client ID already exists");
            await LoadAsync();
            return Page();
        }

        var entity = new Client
        {
            ClientId = Input.ClientId,
            ClientName = string.IsNullOrWhiteSpace(Input.ClientName) ? null : Input.ClientName,
            RealmId = Input.RealmId,
            RequirePkce = Input.RequirePkce,
            RequireConsent = Input.RequireConsent,
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
            ClientSecretHash = string.IsNullOrEmpty(Input.ClientSecret) ? null : hasher.Hash(Input.ClientSecret)
#pragma warning restore CS0618
        };
        db.Clients.Add(entity);
        await db.SaveChangesAsync();
        return TenantAwareRedirectToPage();
    }

    public async Task<IActionResult> OnPostGenerateAsync()
    {
        if (Input is null)
        {
            Input = new ClientInput();
        }
        Input.ClientId = idGen.Generate(24);
        // Ensure generated value shows up despite existing ModelState
        ModelState.Remove("Input.ClientId");
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirectToPage();
        }

        var entity = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == currentTenantId.Value);
        if (entity is null) return TenantAwareRedirectToPage();

        // Capture for cache invalidation
        var clientId = entity.ClientId;
        var tenantId = entity.TenantId;

        db.Clients.Remove(entity);
        await db.SaveChangesAsync();

        // Invalidate client cache after deletion
        await clientStore.InvalidateClientCacheAsync(clientId, tenantId);

        return TenantAwareRedirect("/Admin/Clients");
    }

    public sealed class ClientInput
    {
        [Required, StringLength(200, MinimumLength = 2)]
        public string ClientId { get; set; } = string.Empty;
        [StringLength(200)]
        public string? ClientName { get; set; }
        [Required]
        public Guid RealmId { get; set; }
        public bool RequirePkce { get; set; } = true;
        public bool RequireConsent { get; set; } = true;
        [DataType(DataType.Password)]
        public string? ClientSecret { get; set; }
    }
}
