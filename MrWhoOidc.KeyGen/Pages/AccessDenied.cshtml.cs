using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MrWhoOidc.KeyGen.Configuration;

namespace MrWhoOidc.KeyGen.Pages;

/// <summary>
/// Shown when a signed-in user lacks the required role. Anonymous so the fallback policy
/// does not loop back into another challenge.
/// </summary>
[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public AccessDeniedModel(IOptions<KeyGenAuthOptions> authOptions)
    {
        RequiredRole = authOptions.Value.RequiredRole;
    }

    public string RequiredRole { get; }

    public void OnGet()
    {
    }
}
