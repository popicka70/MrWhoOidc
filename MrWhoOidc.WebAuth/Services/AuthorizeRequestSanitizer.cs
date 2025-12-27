using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Protocols;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MrWhoOidc.WebAuth.Services;

public sealed class AuthorizeRequestSanitizer : IAuthorizeRequestSanitizer
{
    public IResult? SanitizeAddressBar(HttpContext http)
    {
        string? requestUriRaw = http.Request.Query[OAuthConstants.Parameters.RequestUri];
        if (string.IsNullOrEmpty(requestUriRaw))
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OAuthConstants.Parameters.RequestUri, // required for PAR
            OAuthConstants.Parameters.State,       // allowed by RFC 9101
            OidcConstants.Claims.Idp,         // our custom provider selector
            "idp_hint",    // our custom hint
            "qr",          // QR login flow hint
            OidcConstants.Parameters.LoginHint,  // standard hints we want to preserve visually
            OidcConstants.Parameters.AcrValues,
            OidcConstants.Parameters.Prompt,
            OidcConstants.Parameters.UiLocales,
            OidcConstants.Parameters.MaxAge
        };

        var keys = http.Request.Query.Keys.Select(k => k.ToString());
        if (keys.Except(allowed, StringComparer.OrdinalIgnoreCase).Any())
        {
            var baseUrl = http.Request.Path;
            var builder = new System.Text.StringBuilder("?request_uri=");
            builder.Append(Uri.EscapeDataString(requestUriRaw));

            foreach (var name in allowed.Where(n => !string.Equals(n, OAuthConstants.Parameters.RequestUri, StringComparison.OrdinalIgnoreCase)))
            {
                var val = http.Request.Query[name].ToString();
                if (!string.IsNullOrEmpty(val))
                {
                    builder.Append('&');
                    builder.Append(name);
                    builder.Append('=');
                    builder.Append(Uri.EscapeDataString(val));
                }
            }

            return Results.Redirect(baseUrl + builder.ToString());
        }

        return null;
    }
}
