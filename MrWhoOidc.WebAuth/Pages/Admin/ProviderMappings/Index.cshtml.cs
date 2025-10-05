using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderMappings;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : PageModel
{
    public sealed record Row(Guid ClientId, Guid IdentityProviderId, string ClientIdVal, string? ClientName, string ProviderName, bool Enabled, bool IsDefaultForClient, bool AutoRedirectIfSingle, string? RequiredAcr, int Order);

    public List<SelectListItem> ClientOptions { get; private set; } = new();
    public List<SelectListItem> ProviderOptions { get; private set; } = new();
    public List<SelectListItem> TenantOptions { get; private set; } = new();
    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public bool IsPlatformAdmin { get; private set; }
    
    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

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
        
        // Determine tenant scope
        Guid? scopeTenantId;
        if (IsPlatformAdmin)
        {
            scopeTenantId = TenantId;
        }
        else
        {
            scopeTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (!scopeTenantId.HasValue)
            {
                ClientOptions = new List<SelectListItem>();
                ProviderOptions = new List<SelectListItem>();
                Rows = Array.Empty<Row>();
                return;
            }
        }
        
        // Load clients and providers scoped to tenant
        var clientsQuery = db.Clients.AsNoTracking();
        var providersQuery = db.IdentityProviders.AsNoTracking();
        
        if (scopeTenantId.HasValue)
        {
            clientsQuery = clientsQuery.Where(c => c.TenantId == scopeTenantId.Value);
            providersQuery = providersQuery.Where(p => p.TenantId == scopeTenantId.Value);
        }
        
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
            .Join(db.IdentityProviders, cc => cc.cip.IdentityProviderId, p => p.Id, (cc, p) => new { cc.cip, cc.c, p });
        
        if (scopeTenantId.HasValue)
        {
            mappingsQuery = mappingsQuery.Where(x => x.c.TenantId == scopeTenantId.Value);
        }
        
        Rows = await mappingsQuery
            .OrderBy(x => x.c.ClientId).ThenBy(x => x.cip.Order)
            .Select(x => new Row(x.c.Id, x.cip.IdentityProviderId, x.c.ClientId, x.c.ClientName, x.p.DisplayName ?? x.p.Name, x.cip.Enabled, x.cip.IsDefaultForClient, x.cip.AutoRedirectIfSingle, x.cip.RequiredAcr, x.cip.Order))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
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
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid clientId, Guid identityProviderId)
    {
        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == clientId && m.IdentityProviderId == identityProviderId);
        if (entity is not null)
        {
            db.ClientIdentityProviders.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage();
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
