using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages;

public class NotFoundModel : PageModel
{
    public string? RequestedPath { get; set; }
    
    public void OnGet(string? path = null)
    {
        // Get original path from query string (set by UseStatusCodePagesWithReExecute)
        // or from route parameter
        RequestedPath = Request.Query["path"].ToString();
        if (string.IsNullOrEmpty(RequestedPath))
        {
            RequestedPath = path ?? HttpContext.Request.Path.Value;
        }
    }
}
