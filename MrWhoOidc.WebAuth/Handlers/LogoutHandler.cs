using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ILogoutHandler
{
    Task<IResult> LocalLogoutAsync(HttpContext http);
    Task<IResult> EndSessionAsync(HttpContext http);
}

public sealed class LogoutHandler(AuthDbContext db) : ILogoutHandler
{
    public async Task<IResult> LocalLogoutAsync(HttpContext http)
    {
        await http.SignOutAsync();
        var returnUrl = http.Request.Query["returnUrl"].ToString();
        return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }

    public async Task<IResult> EndSessionAsync(HttpContext http)
    {
        await http.SignOutAsync();
        var postLogout = http.Request.Query["post_logout_redirect_uri"].ToString();
        if (!string.IsNullOrEmpty(postLogout))
        {
            return Results.Redirect(postLogout);
        }
        return Results.Redirect("/");
    }
}
