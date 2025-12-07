using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.OidcDemo.Pages;

[Authorize]
public class SecureModel : PageModel
{
    public Dictionary<string, string> Tokens { get; private set; } = new();

    public async Task OnGetAsync()
    {
        // Retrieve stored tokens
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        var idToken = await HttpContext.GetTokenAsync("id_token");
        var refreshToken = await HttpContext.GetTokenAsync("refresh_token");
        var expiresAt = await HttpContext.GetTokenAsync("expires_at");

        if (!string.IsNullOrEmpty(accessToken))
            Tokens["access_token"] = accessToken;
        if (!string.IsNullOrEmpty(idToken))
            Tokens["id_token"] = idToken;
        if (!string.IsNullOrEmpty(refreshToken))
            Tokens["refresh_token"] = refreshToken;
        if (!string.IsNullOrEmpty(expiresAt))
            Tokens["expires_at"] = expiresAt;
    }
}
