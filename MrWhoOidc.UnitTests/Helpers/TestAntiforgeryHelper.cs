using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace MrWhoOidc.UnitTests.Helpers;

internal static class TestAntiforgeryHelper
{
    internal static DefaultHttpContext ProtectPost(DefaultHttpContext http)
    {
        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        var page = new DefaultHttpContext { RequestServices = http.RequestServices, User = http.User };
        page.Request.Scheme = "https";
        page.Request.Host = new HostString("idp.example");
        var tokens = antiforgery.GetAndStoreTokens(page);
        var cookie = page.Response.Headers.SetCookie.ToString().Split(';')[0];
        var existingCookie = http.Request.Headers.Cookie.ToString();
        http.Request.Headers.Cookie = string.IsNullOrEmpty(existingCookie) ? cookie : $"{existingCookie}; {cookie}";
        http.Request.Method = "POST";
        http.Request.Scheme = "https";
        http.Request.Host = page.Request.Host;
        var form = http.Request.HasFormContentType
            ? http.Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value)
            : new Dictionary<string, StringValues>();
        form[tokens.FormFieldName] = tokens.RequestToken;
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(form);
        return http;
    }

    /// <summary>
    /// M-CSRF: adds a valid antiforgery cookie and request token to an HTTP request, the way a browser does.
    /// A cookie-authenticated client that mutates the admin API must send them; a bearer-authenticated client
    /// (the CLI) does not need them.
    /// </summary>
    /// <param name="request">The request to protect.</param>
    /// <param name="services">The application's service provider (the antiforgery service lives there).</param>
    /// <param name="userId">
    /// The id of the authenticated user (the antiforgery token is bound to it), when the request is
    /// authenticated. The token must be generated for the same identity as the request.
    /// </param>
    /// <param name="username">The name of the authenticated user.</param>
    internal static HttpRequestMessage AddToken(
        HttpClient client,
        HttpRequestMessage request,
        IServiceProvider services,
        string? userId = null,
        string? username = null)
    {
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var options = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        var page = new DefaultHttpContext { RequestServices = services };
        if (!string.IsNullOrEmpty(userId) || !string.IsNullOrEmpty(username))
        {
            var claims = new List<Claim>();
            if (!string.IsNullOrEmpty(userId))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
            }
            if (!string.IsNullOrEmpty(username))
            {
                claims.Add(new Claim(ClaimTypes.Name, username));
            }

            page.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        // The antiforgery cookie is bound to the host, so it must be generated for the host the request goes to.
        var requestUri = request.RequestUri is not null && request.RequestUri.IsAbsoluteUri
            ? request.RequestUri
            : client.BaseAddress;

        if (requestUri is null)
        {
            page.Request.Scheme = "https";
            page.Request.Host = new HostString("localhost");
        }
        else
        {
            page.Request.Scheme = requestUri.Scheme;
            page.Request.Host = new HostString(requestUri.Host, requestUri.Port);
        }

        var tokens = antiforgery.GetAndStoreTokens(page);
        var cookie = page.Response.Headers.SetCookie.ToString().Split(';')[0];

        var existing = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : string.Empty;
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            string.IsNullOrEmpty(existing) ? cookie : $"{existing}; {cookie}");

        request.Headers.Remove(options.HeaderName);
        request.Headers.TryAddWithoutValidation(options.HeaderName, tokens.RequestToken ?? string.Empty);

        return request;
    }
}
