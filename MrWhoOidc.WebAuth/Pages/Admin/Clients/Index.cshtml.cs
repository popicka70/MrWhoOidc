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
public class IndexModel(AuthDbContext db, IPasswordHasher hasher, IClientIdGenerator idGen) : PageModel
{
    public sealed record ClientRow(Guid Id, string ClientId, string? ClientName, string RealmName, bool RequirePkce, bool RequireConsent);

    public IReadOnlyList<ClientRow> Clients { get; private set; } = Array.Empty<ClientRow>();

    public List<SelectListItem> RealmOptions { get; private set; } = new();

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();

        Clients = await db.Clients.AsNoTracking()
            .OrderBy(c => c.ClientId)
            .Select(c => new ClientRow(c.Id, c.ClientId, c.ClientName!, db.Realms.Where(r => r.Id == c.RealmId).Select(r => r.Name).First(), c.RequirePkce, c.RequireConsent))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        // Unique client id
        if (await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId))
        {
            ModelState.AddModelError("Input.ClientId", "Client ID already exists");
            await LoadAsync();
            return Page();
        }

        var entity = new Client
        {
            ClientId = Input.ClientId,
            ClientName = string.IsNullOrWhiteSpace(Input.ClientName) ? null : Input.ClientName,
            RealmId = Input.RealmId,
            RequirePkce = Input.RequirePkce,
            RequireConsent = Input.RequireConsent,
            ClientSecretHash = string.IsNullOrEmpty(Input.ClientSecret) ? null : hasher.Hash(Input.ClientSecret)
        };
        db.Clients.Add(entity);
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGenerateAsync()
    {
        if (Input is null)
        {
            Input = new ClientInput();
        }
        Input.ClientId = idGen.Generate(24);
        // Ensure generated value shows up despite existing ModelState
        ModelState.Remove("Input.ClientId");
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var entity = await db.Clients.FirstOrDefaultAsync(c => c.Id == id);
        if (entity is null) return RedirectToPage();
        db.Clients.Remove(entity);
        await db.SaveChangesAsync();
        return RedirectToPage();
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
