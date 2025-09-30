using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Client.Discovery;

namespace MrWhoOidc.RazorClient.Pages;

public class IndexModel : PageModel
{
    private readonly IMrWhoDiscoveryClient _discoveryClient;

    public IndexModel(IMrWhoDiscoveryClient discoveryClient)
    {
        _discoveryClient = discoveryClient;
    }

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;
    public string? UserName => User?.Identity?.Name;
    public IReadOnlyList<KeyValuePair<string, string>> Claims { get; private set; } = Array.Empty<KeyValuePair<string, string>>();
    public IReadOnlyList<KeyValuePair<string, string>> Tokens { get; private set; } = Array.Empty<KeyValuePair<string, string>>();
    public MrWhoDiscoveryDocument? Discovery { get; private set; }

    public async Task OnGetAsync()
    {
        Discovery = await _discoveryClient.GetAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        if (!IsAuthenticated)
        {
            return;
        }

        var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        var tokens = result?.Properties?.GetTokens();
        if (tokens is not null)
        {
            Tokens = tokens.Select(t => new KeyValuePair<string, string>(t.Name, t.Value ?? string.Empty)).ToList();
        }

        if (User.Identity is ClaimsIdentity identity)
        {
            Claims = identity.Claims
                .Select(c => new KeyValuePair<string, string>(c.Type, c.Value))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
        }
    }
}
