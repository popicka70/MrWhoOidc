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
    IAuthorizationService authorizationService,
    IClientStore clientStore) : TenantAwarePageModel(tenantAccessor)
{
    public sealed record ClientRow(Guid Id, string ClientId, string? ClientName, string RealmName, Guid TenantId, string TenantName, bool RequirePkce, bool RequireConsent, bool HasJwks, bool RequirePar);

    public IReadOnlyList<ClientRow> Clients { get; private set; } = Array.Empty<ClientRow>();

    public List<SelectListItem> RealmOptions { get; private set; } = new();
    public List<SelectListItem> TenantOptions { get; private set; } = new();

    public bool IsPlatformAdmin { get; private set; }

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
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

        var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();

        // Build query with tenant and realm JOINs
        var q = db.Clients.AsNoTracking()
            .Join(db.Tenants, c => c.TenantId, t => t.Id, (c, t) => new { Client = c, Tenant = t })
            .Join(db.Realms, x => x.Client.RealmId, r => r.Id, (x, r) => new { x.Client, x.Tenant, Realm = r });

        // Automatic tenant scoping
        if (IsPlatformAdmin)
        {
            // Platform admins can optionally filter by tenant
            if (TenantId.HasValue)
            {
                q = q.Where(x => x.Client.TenantId == TenantId.Value);
            }
        }
        else
        {
            // Regular tenant admins only see their tenant
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (currentTenantId.HasValue)
            {
                q = q.Where(x => x.Client.TenantId == currentTenantId.Value);
            }
            else
            {
                // No tenant context, return empty
                Clients = Array.Empty<ClientRow>();
                return;
            }
        }

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
            ClientSecretHash = string.IsNullOrEmpty(Input.ClientSecret) ? null : hasher.Hash(Input.ClientSecret)
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
        var entity = await db.Clients.FirstOrDefaultAsync(c => c.Id == id);
        if (entity is null) return TenantAwareRedirectToPage();
        
        // Capture for cache invalidation
        var clientId = entity.ClientId;
        var tenantId = entity.TenantId;
        
        db.Clients.Remove(entity);
        await db.SaveChangesAsync();
        
        // Invalidate client cache after deletion
        await clientStore.InvalidateClientCacheAsync(clientId, tenantId);
        
        return TenantAwareRedirect("/Admin/Clients", TenantId.HasValue ? new { TenantId } : null);
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
