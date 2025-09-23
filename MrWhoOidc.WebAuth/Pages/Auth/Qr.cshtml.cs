using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages.Auth;

[AllowAnonymous]
public class QrModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    public void OnGet()
    {
    }
}
