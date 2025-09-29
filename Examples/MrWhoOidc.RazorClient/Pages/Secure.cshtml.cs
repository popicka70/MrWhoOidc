using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.RazorClient.Pages;

[Authorize]
public class SecureModel : PageModel
{
    public void OnGet()
    {
    }
}
