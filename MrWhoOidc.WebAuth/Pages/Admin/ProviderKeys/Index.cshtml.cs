using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderKeys;

[Authorize(Policy = "admin")]
public class IndexModel(AuthDbContext db) : PageModel
{
    public sealed record Row(Guid Id, string Purpose, string Alg, string? Kid, bool Active, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Guid ProviderId { get; private set; }
    public string ProviderDisplay { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    public async Task<IActionResult> OnGetAsync(Guid providerId)
    {
        ProviderId = providerId;
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId);
        if (provider is null) return NotFound();
        ProviderDisplay = provider.DisplayName ?? provider.Name;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(Guid providerId)
    {
        ProviderId = providerId;
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId);
        if (provider is null) return NotFound();
        ProviderDisplay = provider.DisplayName ?? provider.Name;

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        // Validate JSON and basic JWK shape
        try { using var _ = JsonDocument.Parse(Input.JwkJson ?? "{}"); }
        catch (Exception ex)
        {
            ModelState.AddModelError("Input.JwkJson", $"Invalid JSON: {ex.Message}");
            await LoadAsync();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Input.Kid))
            Input.Kid = Guid.NewGuid().ToString("N");

        // kid uniqueness per provider
        var kidExists = await db.IdentityProviderKeys.AnyAsync(k => k.IdentityProviderId == providerId && k.Kid == Input.Kid);
        if (kidExists)
        {
            ModelState.AddModelError("Input.Kid", "Key ID (kid) already exists for this provider.");
            await LoadAsync();
            return Page();
        }

        var entity = new IdentityProviderKey
        {
            IdentityProviderId = providerId,
            Purpose = Enum.TryParse<IdentityProviderKeyPurpose>(Input.Purpose, out var p) ? p : IdentityProviderKeyPurpose.Signing,
            Jwk = Input.JwkJson!,
            Alg = Input.Alg ?? "RS256",
            Active = Input.Active,
            Kid = string.IsNullOrWhiteSpace(Input.Kid) ? null : Input.Kid,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = null
        };
        db.IdentityProviderKeys.Add(entity);

        if (Input.Active)
        {
            // Deactivate other keys of same purpose when marking this one active
            var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync();
            foreach (var o in others) o.Active = false;
        }

        await db.SaveChangesAsync();
        Message = "Key imported.";
        ModelState.Clear();
        Input = new();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostActivateAsync(Guid id, Guid providerId)
    {
        ProviderId = providerId;
        var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId);
        if (entity is null) return NotFound();
        var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync();
        foreach (var o in others) o.Active = false;
        entity.Active = true;
        await db.SaveChangesAsync();
        Message = "Key activated.";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, Guid providerId)
    {
        ProviderId = providerId;
        var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId);
        if (entity is not null)
        {
            db.IdentityProviderKeys.Remove(entity);
            await db.SaveChangesAsync();
            Message = "Key deleted.";
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Rows = await db.IdentityProviderKeys.AsNoTracking()
            .Where(k => k.IdentityProviderId == ProviderId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new Row(k.Id, k.Purpose.ToString(), k.Alg, k.Kid, k.Active, k.CreatedAt, k.ExpiresAt))
            .ToListAsync();
    }

    public sealed class InputModel
    {
        [Required]
        public string Purpose { get; set; } = "Signing"; // enum name
        [Required, StringLength(20)]
        public string Alg { get; set; } = "RS256";
        [StringLength(200)]
        public string? Kid { get; set; }
        public bool Active { get; set; } = true;
        [Required]
        public string JwkJson { get; set; } = string.Empty; // private JWK JSON
    }
}
