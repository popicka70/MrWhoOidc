using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Security.Cryptography;
using System.Text;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Pages.Auth.Providers;

public class SelectModel(AuthDbContext db) : PageModel
{
    public sealed record Item(string Name, string Display, string? LogoUrl, bool IsRecommended = false);

    [BindProperty(SupportsGet = true)]
    public string? Client_Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    [BindProperty(SupportsGet = true)]
    public string? Idp_Hint { get; set; }

    // Optional style override via query: style=classic|ocean|forest|plum|contrast
    [BindProperty(SupportsGet = true, Name = "style")]
    public string? StyleKey { get; set; }

    // Client default style from DB
    public string? ClientStyleKey { get; private set; }

    // Client info for the view
    public string? ClientName { get; private set; }
    public string? RealmName { get; private set; }

    public List<Item> Providers { get; private set; } = new();

    public string ReturnUrlEncoded => Uri.EscapeDataString(ReturnUrl ?? "/");

    public string? Error { get; private set; }

    [BindProperty(SupportsGet = true, Name = "info")]
    public string? Info { get; set; }

    [BindProperty(SupportsGet = true, Name = "cid")]
    public string? CorrelationId { get; set; }

    public bool AllowLocalLogin { get; private set; }
    public bool AllowQrLogin { get; private set; }

    public string? LastProvider { get; private set; }
    public string? RecommendationSource { get; private set; }
    public string? RecommendationAriaDescription { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // DEBUG: Log ReturnUrl for QR troubleshooting
        Console.WriteLine($"[ProviderSelect] OnGetAsync: ReturnUrl={ReturnUrl}, Client_Id={Client_Id}");

        if (string.IsNullOrWhiteSpace(Client_Id))
        {
            // Auto-fallback to admin client when client_id is not specified
            var defCid = await db.ResolveDefaultClientIdAsync();
            if (string.IsNullOrWhiteSpace(defCid))
            {
                Error = "Missing client_id.";
                return Page();
            }
            Client_Id = defCid;
        }

        // DEBUG: Log ReturnUrl for QR troubleshooting
        Console.WriteLine($"[ProviderSelect] ReturnUrl={ReturnUrl}, Client_Id={Client_Id}");

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == Client_Id);
        if (client is null)
        {
            // If unknown, try falling back to admin client
            var defCid = await db.ResolveDefaultClientIdAsync();
            if (string.IsNullOrWhiteSpace(defCid))
            {
                Error = "Unknown client.";
                return Page();
            }
            Client_Id = defCid;
            client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == Client_Id);
            if (client is null)
            {
                Error = "Unknown client.";
                return Page();
            }
        }

        AllowLocalLogin = client.AllowLocalLogin;
        AllowQrLogin = client.AllowQrLogin;
        ClientStyleKey = client.LoginStyleKey; // may be null
        ClientName = string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientId : client.ClientName;
        RealmName = await db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefaultAsync();

        // Read last-used cookie (hash of client_id consistent with Authorize pipeline)
        LastProvider = TryGetLastProviderCookie(Client_Id!);

        var providerLinks = await db.ClientIdentityProviders.AsNoTracking()
            .Where(m => m.ClientId == client.Id && m.Enabled)
            .Join(db.IdentityProviders.AsNoTracking().Where(p => p.Enabled), m => m.IdentityProviderId, p => p.Id, (m, p) => new { m, p })
            .OrderBy(x => x.m.Order)
            .Select(x => new Item(x.p.Name, x.p.DisplayName ?? x.p.Name, x.p.LogoUrl, false))
            .ToListAsync();

        // Determine suggested provider
        string? suggested = null;
        if (!string.IsNullOrWhiteSpace(Idp_Hint) && providerLinks.Any(p => string.Equals(p.Name, Idp_Hint, StringComparison.Ordinal)))
        {
            suggested = Idp_Hint;
            RecommendationSource = "hint";
        }
        else if (!string.IsNullOrWhiteSpace(LastProvider) && providerLinks.Any(p => string.Equals(p.Name, LastProvider, StringComparison.Ordinal)))
        {
            suggested = LastProvider;
            RecommendationSource = "last";
        }

        if (suggested is not null)
        {
            // Mark recommended and move to top for convenience
            providerLinks = providerLinks
                .Select(p => p with { IsRecommended = string.Equals(p.Name, suggested, StringComparison.Ordinal) })
                .OrderByDescending(p => p.IsRecommended)
                .ThenBy(p => p.Display)
                .ToList();

            // Build SR-friendly description summarizing why it is recommended
            if (!string.IsNullOrWhiteSpace(RecommendationSource))
            {
                RecommendationAriaDescription = RecommendationSource switch
                {
                    "last" => "Recommended based on your last sign-in choice.",
                    "hint" => "Recommended based on a provided sign-in hint.",
                    _ => "Recommended provider"
                };
            }
        }

        Providers = providerLinks;

        // If auto=1 and single provider, immediately choose it but only when local login is not allowed
        if (Request.Query.TryGetValue("auto", out var autoVal) && autoVal == "1" && Providers.Count == 1 && !AllowLocalLogin)
        {
            return await ChooseAsync(Providers[0].Name);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostChooseAsync(string provider)
    {
        return await ChooseAsync(provider);
    }

    private async Task<IActionResult> ChooseAsync(string provider)
    {
        if (string.IsNullOrWhiteSpace(Client_Id))
        {
            Client_Id = await db.ResolveDefaultClientIdAsync();
        }
        if (string.IsNullOrWhiteSpace(Client_Id) || string.IsNullOrWhiteSpace(ReturnUrl))
        {
            Error = "Missing parameters.";
            return Page();
        }

        // Legacy cookie (kept for compatibility if this POST flow is used)
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
        // Also set the new hashed cookie name used by the authorize pipeline
        var hashedName = BuildLastProviderCookieName(Client_Id!);
        Response.Cookies.Append(hashedName, provider, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(90)
        });

        // Redirect back to authorize with idp selected
        var sep = ReturnUrl!.Contains("?", StringComparison.Ordinal) ? "&" : "?";
        var url = ReturnUrl + sep + "idp=" + Uri.EscapeDataString(provider);
        return Redirect(url);
    }

    private static string BuildLastProviderCookieName(string clientId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
        var bucket = Convert.ToHexString(bytes.AsSpan(0, 8));
        return ".mrwhooidc.lastidp." + bucket;
    }

    private string? TryGetLastProviderCookie(string clientId)
    {
        var name = BuildLastProviderCookieName(clientId);
        if (Request.Cookies.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
            return v;
        return null;
    }
}
