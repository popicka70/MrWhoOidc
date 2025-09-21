using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Registrations;

[AllowAnonymous]
public class IndexModel(AuthDbContext db, IPasswordHasher hasher) : PageModel
{
    public List<SelectListItem> ClientOptions { get; private set; } = new();

    [BindProperty]
    public RegistrationInput Input { get; set; } = new();

    public string? SuccessMessage { get; private set; }
    public string? InfoMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadClientsAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadClientsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var email = Input.Email.Trim().ToLowerInvariant();

        // If user exists already, reject with a warning
        var userExists = await db.Users.AsNoTracking().AnyAsync(u => u.Email == email);
        if (userExists)
        {
            ModelState.AddModelError(string.Empty, "A user with this email already exists. Registration rejected.");
            return Page();
        }

        // If there's already a pending registration for this email, skip creating a duplicate
        var pending = await db.Set<Registration>().AsNoTracking().FirstOrDefaultAsync(r => r.Email == email && r.State == "pending");
        if (pending is not null)
        {
            InfoMessage = "A pending registration already exists for this email.";
            return Page();
        }

        string? passwordHash = null;
        if (!string.IsNullOrWhiteSpace(Input.Password))
        {
            passwordHash = hasher.Hash(Input.Password);
        }

        var entity = new Registration
        {
            Email = email,
            FirstName = string.IsNullOrWhiteSpace(Input.FirstName) ? null : Input.FirstName.Trim(),
            LastName = string.IsNullOrWhiteSpace(Input.LastName) ? null : Input.LastName.Trim(),
            ClientId = Input.ClientId,
            PasswordHash = passwordHash,
            State = "pending",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Set<Registration>().Add(entity);
        await db.SaveChangesAsync();
        SuccessMessage = "Registration submitted. You'll be notified when it's approved.";
        ModelState.Clear();
        Input = new();
        return Page();
    }

    private async Task LoadClientsAsync()
    {
        var clients = await db.Clients.AsNoTracking()
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { c.Id, c.ClientId, RealmName = r.Name })
            .OrderBy(x => x.ClientId).ToListAsync();
        ClientOptions = clients.Select(c => new SelectListItem($"{c.ClientId} ({c.RealmName})", c.Id.ToString())).ToList();
    }

    public sealed class RegistrationInput
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = string.Empty;
        [StringLength(100)]
        public string? FirstName { get; set; }
        [StringLength(100)]
        public string? LastName { get; set; }
        public Guid? ClientId { get; set; }
        [StringLength(200)]
        [DataType(DataType.Password)]
        public string? Password { get; set; }
    }
}
