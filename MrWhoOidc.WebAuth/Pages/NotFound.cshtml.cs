using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages;

public class NotFoundModel : PageModel
{
    public string? RequestedPath { get; set; }
    
    public IActionResult OnGet(string? path = null)
    {
        // Get original path from query string (set by UseStatusCodePagesWithReExecute)
        // or from route parameter
        RequestedPath = Request.Query["path"].ToString();
        if (string.IsNullOrEmpty(RequestedPath))
        {
            RequestedPath = path ?? HttpContext.Request.Path.Value;
        }

        // IMPORTANT: If this is an API/OIDC protocol endpoint, return raw 404
        // These endpoints should not render HTML pages
        if (!string.IsNullOrEmpty(RequestedPath))
        {
            var apiPaths = new[] { "/token", "/userinfo", "/revoke", "/introspect", "/par", "/jwks", "/.well-known", "/api" };
            if (apiPaths.Any(p => RequestedPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                Response.Clear();
                Response.StatusCode = 404;
                Response.ContentType = "application/json";
                return Content("{\"error\":\"not_found\",\"error_description\":\"The requested resource was not found.\"}");
            }
        }

        // For user-facing pages, render the NotFound Razor page
        return Page();
    }
}

