using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

/// <summary>
/// Edit OAuth/OIDC scopes.
/// NOTE: Scopes are GLOBAL resources shared across all tenants (no TenantId).
/// Only platform administrators can edit scopes to prevent tenant admins from modifying
/// shared resources like "openid", "profile", "email", etc.
/// </summary>
[Authorize(Policy = "platform-admin")]
public class EditModel(AuthDbContext db, ITenantAccessor tenantAccessor) : TenantAwarePageModel(tenantAccessor)
{
    public class EditInput
    {
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? Description { get; set; }
        public bool IsExposed { get; set; } = true;
    }

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return TenantAwareRedirect("/Admin/Scopes");
        var entity = await db.Scopes.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name);
        if (entity is null) return TenantAwareRedirect("/Admin/Scopes");
        Input = new EditInput { Name = entity.Name, Description = entity.Description, IsExposed = entity.IsExposed };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string name)
    {
        if (!ModelState.IsValid) return Page();
        var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
        if (entity is null) return TenantAwareRedirect("/Admin/Scopes");
        entity.Description = Input.Description;
        entity.IsExposed = Input.IsExposed;
        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/Scopes");
    }
}
