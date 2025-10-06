using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

[Authorize(Policy = "tenant-admin")]
public class EditModel(AuthDbContext db) : ReadOnlyAdminPageModel
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
        if (string.IsNullOrWhiteSpace(name)) return RedirectToPage("Index");
        var entity = await db.Scopes.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name);
        if (entity is null) return RedirectToPage("Index");
        Input = new EditInput { Name = entity.Name, Description = entity.Description, IsExposed = entity.IsExposed };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string name)
    {
        if (!ModelState.IsValid) return Page();
        var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
        if (entity is null) return RedirectToPage("Index");
        entity.Description = Input.Description;
        entity.IsExposed = Input.IsExposed;
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
