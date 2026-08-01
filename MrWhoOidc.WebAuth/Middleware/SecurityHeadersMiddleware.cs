using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;

namespace MrWhoOidc.WebAuth.Middleware;

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        // Generate nonce early so it is available during Razor view rendering.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        context.Items["csp-nonce"] = nonce;

        context.Response.OnStarting(() =>
        {
            // Apply to HTML responses only to reduce risk of breaking protocol endpoints.
            var contentType = context.Response.ContentType;
            if (contentType is not null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var headers = context.Response.Headers;

                // OIDC Session Management check_session_iframe must be embeddable by relying parties.
                // Do not apply frame-deny headers to this specific HTML response.
                var isCheckSessionIFrame = context.Request.Path.Value is not null &&
                                          context.Request.Path.Value.EndsWith("/connect/checksession", StringComparison.OrdinalIgnoreCase);

                // form_post response_mode delivers the OIDC response via an auto-submitting form
                // to the registered redirect_uri. The "form-action 'self'" directive would block
                // the browser from POSTing to the relying-party host, breaking the protocol.
                // Detect the generated form_post page via the marker header.
                var isFormPostResponse = headers.ContainsKey("X-Form-Post-Response");

                // Baseline safe headers
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                headers.TryAdd("Permissions-Policy", "geolocation=(), microphone=(), camera=(), payment=(), usb=()");
                headers.TryAdd("X-XSS-Protection", "1; mode=block");

                var scriptSrc = $"script-src 'self' 'nonce-{nonce}'";
                var styleSrc = $"style-src 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net https://unpkg.com";
                var styleSrcElem = $"style-src-elem 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net https://unpkg.com";
                var styleSrcAttr = "style-src-attr 'unsafe-inline'";
                var fontSrc = "font-src 'self' data: https://cdn.jsdelivr.net https://unpkg.com";
                // Allow form-action to any HTTPS host so registered redirect_uris on other
                // origins can receive the form_post response. "self" would only permit same-origin.
                var formActionSrc = isFormPostResponse ? "form-action 'self' https:" : "form-action 'self'";

                if (isCheckSessionIFrame)
                {
                    headers.TryAdd(
                        "Content-Security-Policy",
                        "default-src 'self'; base-uri 'self'; frame-ancestors https:; object-src 'none'; " +
                        $"{scriptSrc}; {styleSrc}; {styleSrcElem}; {styleSrcAttr}; {fontSrc}; connect-src 'self'");
                }

                if (!isCheckSessionIFrame)
                {
                    headers.TryAdd("X-Frame-Options", "DENY");

                    headers.TryAdd(
                        "Content-Security-Policy",
                        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; " +
                        $"{formActionSrc}; " +
                        "img-src 'self' data: https:; " +
                        $"{styleSrc}; {styleSrcElem}; {styleSrcAttr}; {fontSrc}; " +
                        $"{scriptSrc}; connect-src 'self'");
                }
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
