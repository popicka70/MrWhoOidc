using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages;

[Authorize]
public class ConsentModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ClientId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string[] Scopes { get; set; } = Array.Empty<string>();

    public string CancelUrl => "/"; // could redirect back to app with error
}
