using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ILogoutHandler
{
    Task<IResult> LocalLogoutAsync(HttpContext http);
    Task<IResult> EndSessionAsync(HttpContext http);
}

public sealed class LogoutHandler(OidcOptions options) : ILogoutHandler
{
    public async Task<IResult> LocalLogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var returnUrl = http.Request.Query["returnUrl"].ToString();
        if (!string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        {
            return Results.Redirect(returnUrl);
        }
        return Results.Redirect("/");
    }

    public async Task<IResult> EndSessionAsync(HttpContext http)
    {
        var postLogout = http.Request.Query["post_logout_redirect_uri"].ToString();
        var state = http.Request.Query["state"].ToString();

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (!string.IsNullOrEmpty(postLogout) && IsAllowedPostLogoutUri(postLogout, options.AllowedPostLogoutRedirectUris))
        {
            var uri = new UriBuilder(postLogout);
            if (!string.IsNullOrEmpty(state))
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                query["state"] = state;
                uri.Query = query.ToString();
            }
            return Results.Redirect(uri.ToString());
        }

        return Results.Redirect("/");
    }

    static bool IsAllowedPostLogoutUri(string uri, string[] allowed)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        foreach (var a in allowed)
        {
            if (Uri.TryCreate(a, UriKind.Absolute, out var au))
            {
                if (string.Equals(u.Scheme, au.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(u.Host, au.Host, StringComparison.OrdinalIgnoreCase)
                    && (au.Port == -1 || u.Port == au.Port))
                {
                    if (string.IsNullOrEmpty(au.AbsolutePath) || u.AbsolutePath.StartsWith(au.AbsolutePath, StringComparison.Ordinal))
                        return true;
                }
            }
        }
        return false;
    }
}
