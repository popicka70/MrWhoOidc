using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
}
