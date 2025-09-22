using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderMappings;

[Authorize(Policy = "admin")]
public class IndexModel(AuthDbContext db) : PageModel
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
        ClientOptions = await db.Clients.AsNoTracking()
            .OrderBy(c => c.ClientId)
            .Select(c => new SelectListItem(c.ClientId, c.Id.ToString()))
            .ToListAsync();
        ProviderOptions = await db.IdentityProviders.AsNoTracking()
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new SelectListItem(p.DisplayName ?? p.Name, p.Id.ToString()))
            .ToListAsync();

        Rows = await db.ClientIdentityProviders.AsNoTracking()
            .Join(db.Clients, cip => cip.ClientId, c => c.Id, (cip, c) => new { cip, c })
            .Join(db.IdentityProviders, cc => cc.cip.IdentityProviderId, p => p.Id, (cc, p) => new { cc.cip, cc.c, p })
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
