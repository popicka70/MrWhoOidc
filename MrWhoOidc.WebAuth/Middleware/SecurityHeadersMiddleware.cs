using Microsoft.AspNetCore.Http;

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

                // Baseline safe headers
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");

                if (!isCheckSessionIFrame)
                {
                    headers.TryAdd("X-Frame-Options", "DENY");

                    // Conservative CSP: keep UI working while still tightening key vectors.
                    // (We do not attempt nonce-based CSP here.)
                    headers.TryAdd(
                        "Content-Security-Policy",
                        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; form-action 'self'; " +
                        "img-src 'self' data: https:; " +
                        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; style-src-elem 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
                        "font-src 'self' data: https://cdn.jsdelivr.net; " +
                        "script-src 'self' 'unsafe-inline'; connect-src 'self'");
                }
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
