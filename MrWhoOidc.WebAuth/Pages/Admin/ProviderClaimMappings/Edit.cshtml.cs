using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderClaimMappings;

[Authorize(Policy = "tenant-admin")]
public class EditModel(AuthDbContext db) : PageModel
{
    [BindProperty]
    public InputModel? Input { get; set; }

    public Guid ProviderId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var entity = await db.IdentityProviderClaimMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (entity is null) return NotFound();
        ProviderId = entity.IdentityProviderId;
        Input = new InputModel
        {
            Id = entity.Id,
            ExternalClaim = entity.ExternalClaim,
            LocalClaim = entity.LocalClaim,
            Transform = entity.Transform,
            Order = entity.Order
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid || Input is null) return Page();
        var entity = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == id);
        if (entity is null) return NotFound();
        ProviderId = entity.IdentityProviderId;

        entity.ExternalClaim = Input.ExternalClaim.Trim();
        entity.LocalClaim = Input.LocalClaim.Trim();
        entity.Transform = string.IsNullOrWhiteSpace(Input.Transform) ? null : Input.Transform.Trim();
        entity.Order = Input.Order;
        await db.SaveChangesAsync();
        return RedirectToPage("Index", new { providerId = ProviderId });
    }

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        [Required, StringLength(200)]
        public string ExternalClaim { get; set; } = string.Empty;
        [Required, StringLength(200)]
        public string LocalClaim { get; set; } = string.Empty;
        [StringLength(200)]
        public string? Transform { get; set; }
        public int Order { get; set; } = 0;
    }
}
