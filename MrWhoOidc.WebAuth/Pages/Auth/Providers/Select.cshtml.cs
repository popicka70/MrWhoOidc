using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Auth.Providers;

public class SelectModel(AuthDbContext db) : PageModel
{
    public sealed record Item(string Name, string Display, bool IsDefault);

    [BindProperty(SupportsGet = true)]
    public string? Client_Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Idp_Hint { get; set; }

    public string ReturnUrlEncoded => Uri.EscapeDataString(ReturnUrl ?? "/");

    public string? Error { get; private set; }

    public List<Item> Items { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(Client_Id))
        {
            Error = "Missing client_id.";
            return Page();
        }

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == Client_Id);
        if (client is null)
        {
            Error = "Unknown client.";
            return Page();
        }

        var providerLinks = await db.ClientIdentityProviders.AsNoTracking()
            .Where(m => m.ClientId == client.Id && m.Enabled)
            .Join(db.IdentityProviders.AsNoTracking().Where(p => p.Enabled), m => m.IdentityProviderId, p => p.Id, (m, p) => new { m, p })
            .OrderBy(x => x.m.Order)
            .Select(x => new Item(x.p.Name, x.p.DisplayName ?? x.p.Name, x.m.IsDefaultForClient))
            .ToListAsync();

        Items = providerLinks;

        // If auto=1 and single provider, immediately choose it
        if (Request.Query.TryGetValue("auto", out var autoVal) && autoVal == "1" && Items.Count == 1)
        {
            return await ChooseAsync(Items[0].Name);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostChooseAsync(string provider)
    {
        return await ChooseAsync(provider);
    }

    private async Task<IActionResult> ChooseAsync(string provider)
    {
        if (string.IsNullOrWhiteSpace(Client_Id) || string.IsNullOrWhiteSpace(ReturnUrl))
        {
            Error = "Missing parameters.";
            return Page();
        }

        // Set cookie for this client
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == Client_Id);
        if (client is not null)
        {
            Response.Cookies.Append($"idp-{client.Id:N}", provider, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(30)
            });
        }

        // Redirect back to authorize with idp selected
        var sep = ReturnUrl!.Contains("?", StringComparison.Ordinal) ? "&" : "?";
        var url = ReturnUrl + sep + "idp=" + Uri.EscapeDataString(provider);
        return Redirect(url);
    }
}
