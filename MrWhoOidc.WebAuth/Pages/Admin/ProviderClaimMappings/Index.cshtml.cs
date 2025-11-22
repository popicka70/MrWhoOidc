using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderClaimMappings;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record Row(Guid Id, int Order, string ExternalClaim, string LocalClaim, string? Transform);

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Guid ProviderId { get; private set; }
    public string ProviderDisplay { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    public async Task<IActionResult> OnGetAsync(Guid providerId)
    {
        if (!await LoadProviderAsync(providerId))
        {
            return NotFound();
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid providerId)
    {
        if (!await LoadProviderAsync(providerId))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var entity = new IdentityProviderClaimMapping
        {
            IdentityProviderId = providerId,
            ExternalClaim = Input.ExternalClaim.Trim(),
            LocalClaim = Input.LocalClaim.Trim(),
            Transform = string.IsNullOrWhiteSpace(Input.Transform) ? null : Input.Transform.Trim(),
            Order = Input.Order
        };
        db.IdentityProviderClaimMappings.Add(entity);
        await db.SaveChangesAsync();
        Message = "Mapping added.";
        ModelState.Clear();
        Input = new();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, Guid providerId)
    {
        if (!await LoadProviderAsync(providerId))
        {
            return NotFound();
        }

        var entity = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == id && m.IdentityProviderId == providerId);
        if (entity is not null)
        {
            db.IdentityProviderClaimMappings.Remove(entity);
            await db.SaveChangesAsync();
            Message = "Mapping deleted.";
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Rows = await db.IdentityProviderClaimMappings.AsNoTracking()
            .Where(m => m.IdentityProviderId == ProviderId)
            .OrderBy(m => m.Order)
            .Select(m => new Row(m.Id, m.Order, m.ExternalClaim, m.LocalClaim, m.Transform))
            .ToListAsync();
    }

    private async Task<bool> LoadProviderAsync(Guid providerId)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false;
        }

        var provider = await db.IdentityProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerId && p.TenantId == currentTenantId.Value);
        if (provider is null)
        {
            return false;
        }

        ProviderId = providerId;
        ProviderDisplay = provider.DisplayName ?? provider.Name;
        return true;
    }

    public sealed class InputModel
    {
        [Required, StringLength(200)]
        public string ExternalClaim { get; set; } = string.Empty;
        [Required, StringLength(200)]
        public string LocalClaim { get; set; } = string.Empty;
        [StringLength(200)]
        public string? Transform { get; set; }
        public int Order { get; set; } = 0;
    }
}
