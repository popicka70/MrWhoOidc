using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.OidcDemo.Pages.Account;

public class LoginModel : PageModel
{
    public IActionResult OnGet(string? returnUrl = null)
    {
        // If already authenticated, redirect to home
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Index");
        }

        // Challenge triggers the OIDC middleware to redirect to the IdP
        var redirectUri = returnUrl ?? Url.Content("~/");
        return Challenge(
            new AuthenticationProperties 
            { 
                RedirectUri = redirectUri 
            }, 
            OpenIdConnectDefaults.AuthenticationScheme);
    }
}
