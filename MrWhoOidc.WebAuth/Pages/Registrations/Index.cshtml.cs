using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Registrations;

[AllowAnonymous]
public class IndexModel(AuthDbContext db, IPasswordHasher hasher, ITenantAccessor tenantAccessor) : PageModel
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

        var email = Input.Email.Trim();
        var normalized = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrEmpty(normalized))
        {
            ModelState.AddModelError(nameof(Input.Email), "Invalid email address.");
            return Page();
        }

        // Get current tenant context
        var currentTenant = tenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            ModelState.AddModelError(string.Empty, "Unable to determine tenant context.");
            return Page();
        }

        // If user exists already in this tenant, reject with a warning
        var userExists = await db.Users.AsNoTracking()
            .AnyAsync(u => u.TenantId == currentTenant.TenantId && u.NormalizedEmail == normalized);
        if (userExists)
        {
            ModelState.AddModelError(string.Empty, "A user with this email already exists. Registration rejected.");
            return Page();
        }

        // If there's already a pending registration for this email in this tenant, skip creating a duplicate
        var pending = await db.Set<Registration>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == currentTenant.TenantId && r.NormalizedEmail == normalized && r.State == "pending");
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
            TenantId = currentTenant.TenantId,
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
        var currentTenant = tenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            ClientOptions = new List<SelectListItem>();
            return;
        }

        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.TenantId == currentTenant.TenantId)
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
