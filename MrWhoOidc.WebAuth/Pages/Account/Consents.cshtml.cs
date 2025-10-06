using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Security.Claims;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages.Account;

[Authorize]
public class ConsentsModel(AuthDbContext db) : PageModel
{
    public List<ConsentViewModel> Consents { get; private set; } = new();
    public string? Message { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return;

        // Get active consents with client details
        var consents = await db.Consents
            .AsNoTracking()
            .Where(c => c.UserId == user.Id && c.RevokedAt == null)
            .Join(db.Clients,
                c => c.ClientId,
                cl => cl.ClientId,
                (c, cl) => new { Consent = c, Client = cl })
            .OrderByDescending(x => x.Consent.CreatedAt)
            .ToListAsync();

        Consents = consents.Select(x => new ConsentViewModel
        {
            Id = x.Consent.Id,
            ClientId = x.Consent.ClientId,
            ClientName = x.Client.ClientName ?? x.Consent.ClientId,
            Scopes = ParseScopes(x.Consent.ScopesJson),
            ConsentedAt = x.Consent.CreatedAt,
            TenantId = x.Consent.TenantId
        }).ToList();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid consentId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToPage("/Login", new { returnUrl = Url.Page("/Account/Consents") });

        var consent = await db.Consents
            .FirstOrDefaultAsync(c => c.Id == consentId && c.UserId == user.Id);

        if (consent is null)
        {
            Message = "Consent not found or already revoked.";
            return RedirectToPage();
        }

        if (consent.RevokedAt is null)
        {
            consent.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            Message = "Consent revoked successfully. The application will need to request permission again.";
        }

        return RedirectToPage();
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;

        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
    }

    private string GetCurrentSessionJti()
    {
        return User?.FindFirst("jti")?.Value ?? string.Empty;
    }

    private static List<string> ParseScopes(string? scopesJson)
    {
        if (string.IsNullOrWhiteSpace(scopesJson))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(scopesJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}

public class ConsentViewModel
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTimeOffset ConsentedAt { get; set; }
    public Guid TenantId { get; set; }
}
