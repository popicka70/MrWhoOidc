using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

[Authorize]
public class EditModel(AuthDbContext db, IPasswordHasher hasher) : PageModel
{
    [FromRoute]
    public Guid Id { get; set; }

    public List<SelectListItem> RealmOptions { get; private set; } = new();

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == Id);
        if (client is null) return NotFound();

        var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();

        Input = new ClientInput
        {
            ClientId = client.ClientId,
            ClientName = client.ClientName,
            RealmId = client.RealmId,
            RequirePkce = client.RequirePkce,
            RequireConsent = client.RequireConsent
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
            RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
            return Page();
        }

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == Id);
        if (client is null) return NotFound();

        // If client id changed, enforce uniqueness
        if (!string.Equals(client.ClientId, Input.ClientId, StringComparison.Ordinal))
        {
            var exists = await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId);
            if (exists)
            {
                var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
                RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
                ModelState.AddModelError("Input.ClientId", "Client ID already exists");
                return Page();
            }
        }

        client.ClientId = Input.ClientId;
        client.ClientName = string.IsNullOrWhiteSpace(Input.ClientName) ? null : Input.ClientName;
        client.RealmId = Input.RealmId;
        client.RequirePkce = Input.RequirePkce;
        client.RequireConsent = Input.RequireConsent;
        if (!string.IsNullOrEmpty(Input.ClientSecret))
        {
            client.ClientSecretHash = hasher.Hash(Input.ClientSecret);
        }

        await db.SaveChangesAsync();
        return RedirectToPage("Index");
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
