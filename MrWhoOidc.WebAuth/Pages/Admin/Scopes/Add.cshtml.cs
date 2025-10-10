using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

/// <summary>
/// Add new OAuth/OIDC scopes.
/// NOTE: Scopes are GLOBAL resources shared across all tenants (no TenantId).
/// Only platform administrators can create scopes to prevent tenant admins from polluting
/// the shared scope catalog.
/// </summary>
[Authorize(Policy = "platform-admin")]
public class AddModel(AuthDbContext db) : PageModel
{
    public class AddInput
    {
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? Description { get; set; }
        public bool IsExposed { get; set; } = true;
    }

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        Input.Name = Input.Name.Trim();
        var exists = await db.Scopes.AnyAsync(s => s.Name == Input.Name);
        if (exists)
        {
            ModelState.AddModelError("Input.Name", "Scope already exists.");
            return Page();
        }
        db.Scopes.Add(new Scope { Name = Input.Name, Description = Input.Description, IsExposed = Input.IsExposed });
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
