using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderMappings;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record Row(Guid ClientId, Guid IdentityProviderId, string ClientIdVal, string? ClientName, string ProviderName, bool Enabled, bool IsDefaultForClient, bool AutoRedirectIfSingle, string? RequiredAcr, int Order);

    public List<SelectListItem> ClientOptions { get; private set; } = new();
    public List<SelectListItem> ProviderOptions { get; private set; } = new();
    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var scopeTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!scopeTenantId.HasValue)
        {
            ClientOptions = new List<SelectListItem>();
            ProviderOptions = new List<SelectListItem>();
            Rows = Array.Empty<Row>();
            return;
        }

        // Load clients and providers scoped to tenant
        var clientsQuery = db.Clients.AsNoTracking()
            .Where(c => c.TenantId == scopeTenantId.Value);
        var providersQuery = db.IdentityProviders.AsNoTracking()
            .Where(p => p.TenantId == scopeTenantId.Value);

        ClientOptions = await clientsQuery
            .OrderBy(c => c.ClientId)
            .Select(c => new SelectListItem(c.ClientId, c.Id.ToString()))
            .ToListAsync();
        ProviderOptions = await providersQuery
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new SelectListItem(p.DisplayName ?? p.Name, p.Id.ToString()))
            .ToListAsync();

        var mappingsQuery = db.ClientIdentityProviders.AsNoTracking()
            .Join(db.Clients, cip => cip.ClientId, c => c.Id, (cip, c) => new { cip, c })
            .Join(db.IdentityProviders, cc => cc.cip.IdentityProviderId, p => p.Id, (cc, p) => new { cc.cip, cc.c, p })
            .Where(x => x.c.TenantId == scopeTenantId.Value);

        Rows = await mappingsQuery
            .OrderBy(x => x.c.ClientId).ThenBy(x => x.cip.Order)
            .Select(x => new Row(x.c.Id, x.cip.IdentityProviderId, x.c.ClientId, x.c.ClientName, x.p.DisplayName ?? x.p.Name, x.cip.Enabled, x.cip.IsDefaultForClient, x.cip.AutoRedirectIfSingle, x.cip.RequiredAcr, x.cip.Order))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var scopeTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!scopeTenantId.HasValue)
        {
            ModelState.AddModelError(string.Empty, "Tenant context is required to manage provider mappings.");
            await LoadAsync();
            return Page();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == Input.ClientId && c.TenantId == scopeTenantId.Value);
        if (client is null)
        {
            ModelState.AddModelError("Input.ClientId", "Client not found in current tenant.");
            await LoadAsync();
            return Page();
        }

        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == Input.IdentityProviderId && p.TenantId == scopeTenantId.Value);
        if (provider is null)
        {
            ModelState.AddModelError("Input.IdentityProviderId", "Identity provider not found in current tenant.");
            await LoadAsync();
            return Page();
        }

        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == Input.ClientId && m.IdentityProviderId == Input.IdentityProviderId);
        if (entity is null)
        {
            entity = new ClientIdentityProvider { ClientId = Input.ClientId, IdentityProviderId = Input.IdentityProviderId };
            db.ClientIdentityProviders.Add(entity);
        }
        entity.Enabled = Input.Enabled;
        entity.IsDefaultForClient = Input.IsDefaultForClient;
        entity.AutoRedirectIfSingle = Input.AutoRedirectIfSingle;
        entity.RequiredAcr = string.IsNullOrWhiteSpace(Input.RequiredAcr) ? null : Input.RequiredAcr.Trim();
        entity.Order = Input.Order;

        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/ProviderMappings");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid clientId, Guid identityProviderId)
    {
        var scopeTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!scopeTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/ProviderMappings");
        }

        var entity = await db.ClientIdentityProviders
            .Join(db.Clients, m => m.ClientId, c => c.Id, (m, c) => new { Mapping = m, Client = c })
            .Where(x => x.Mapping.ClientId == clientId && x.Mapping.IdentityProviderId == identityProviderId && x.Client.TenantId == scopeTenantId.Value)
            .Select(x => x.Mapping)
            .FirstOrDefaultAsync();
        if (entity is not null)
        {
            db.ClientIdentityProviders.Remove(entity);
            await db.SaveChangesAsync();
        }
        return TenantAwareRedirect("/Admin/ProviderMappings");
    }

    public sealed class InputModel
    {
        [Required]
        public Guid ClientId { get; set; }
        [Required]
        public Guid IdentityProviderId { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsDefaultForClient { get; set; } = false;
        public bool AutoRedirectIfSingle { get; set; } = false;
        [StringLength(100)]
        public string? RequiredAcr { get; set; }
        public int Order { get; set; } = 0;
    }
}
