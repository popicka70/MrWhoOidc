using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages;

public class NotFoundModel : PageModel
{
    public string? RequestedPath { get; set; }
    
    public void OnGet()
    {
        RequestedPath = HttpContext.Request.Path.Value;
        Response.StatusCode = 404;
    }
}
