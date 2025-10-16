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
public class AddModel(
    AuthDbContext db, 
    IPasswordHasher hasher, 
    IClientIdGenerator idGen, 
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public List<SelectListItem> RealmOptions { get; private set; } = new();

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadRealmsAsync();
    }

    // Explicit create handler to avoid any ambiguity with other submit buttons
    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadRealmsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Unique client id
        if (await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId))
        {
            ModelState.AddModelError("Input.ClientId", "Client ID already exists");
            return Page();
        }

        // Get current tenant ID from context
        var currentTenant = TenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            ModelState.AddModelError(string.Empty, "Unable to determine current tenant context");
            return Page();
        }

        var entity = new Client
        {
            ClientId = Input.ClientId,
            ClientName = string.IsNullOrWhiteSpace(Input.ClientName) ? null : Input.ClientName,
            TenantId = currentTenant.TenantId,
            RealmId = Input.RealmId,
            RequirePkce = Input.RequirePkce,
            RequireConsent = Input.RequireConsent,
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
            ClientSecretHash = string.IsNullOrEmpty(Input.ClientSecret) ? null : hasher.Hash(Input.ClientSecret)
#pragma warning restore CS0618
        };
        db.Clients.Add(entity);
        await db.SaveChangesAsync();
        return TenantAwareRedirect($"/Admin/Clients/Edit/{entity.Id}");
    }

    public async Task<IActionResult> OnPostGenerateAsync()
    {
        await LoadRealmsAsync();
        if (Input is null)
        {
            Input = new ClientInput();
        }
        Input.ClientId = idGen.Generate(24);
        ModelState.Remove("Input.ClientId");
        return Page();
    }

    private async Task LoadRealmsAsync()
    {
        // Get current tenant ID to filter realms
        var currentTenant = TenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            RealmOptions = new List<SelectListItem>();
            return;
        }

        var realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == currentTenant.TenantId)
            .OrderBy(r => r.Name)
            .ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
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
