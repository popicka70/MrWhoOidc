using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "admin")]
public class ClaimMappingsModel(AuthDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public List<Item> Mappings { get; private set; } = new();

    [BindProperty] public EditorInput? Input { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var exists = await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == Id);
        if (!exists) return NotFound();
        Mappings = await db.IdentityProviderClaimMappings.AsNoTracking()
            .Where(m => m.IdentityProviderId == Id)
            .OrderBy(m => m.Order)
            .Select(m => new Item(m.Id, m.ExternalClaim, m.LocalClaim, m.Transform, m.Order))
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (Input is null) return RedirectToPage(new { id = Id });
        if (!ModelState.IsValid) return await OnGetAsync();
        var e = new IdentityProviderClaimMapping
        {
            IdentityProviderId = Id,
            ExternalClaim = Input.ExternalClaim.Trim(),
            LocalClaim = Input.LocalClaim.Trim(),
            Transform = string.IsNullOrWhiteSpace(Input.Transform) ? null : Input.Transform.Trim(),
            Order = Input.Order
        };
        db.IdentityProviderClaimMappings.Add(e);
        await db.SaveChangesAsync();
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid mappingId)
    {
        if (Input is null) return RedirectToPage(new { id = Id });
        if (!ModelState.IsValid) return await OnGetAsync();
        var e = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == mappingId && m.IdentityProviderId == Id);
        if (e is null) return NotFound();
        e.ExternalClaim = Input.ExternalClaim.Trim();
        e.LocalClaim = Input.LocalClaim.Trim();
        e.Transform = string.IsNullOrWhiteSpace(Input.Transform) ? null : Input.Transform.Trim();
        e.Order = Input.Order;
        await db.SaveChangesAsync();
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid mappingId)
    {
        var e = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == mappingId && m.IdentityProviderId == Id);
        if (e is null) return NotFound();
        db.IdentityProviderClaimMappings.Remove(e);
        await db.SaveChangesAsync();
        return RedirectToPage(new { id = Id });
    }

    public sealed record Item(Guid Id, string External, string Local, string? Transform, int Order);

    public sealed class EditorInput
    {
        [Required] public string ExternalClaim { get; set; } = string.Empty;
        [Required] public string LocalClaim { get; set; } = string.Empty;
        public int Order { get; set; }
        public string? Transform { get; set; }
    }
}
