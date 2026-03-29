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

                // Baseline safe headers
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");

                if (!isCheckSessionIFrame)
                {
                    headers.TryAdd("X-Frame-Options", "DENY");

                    // Use per-request nonce for script-src to eliminate 'unsafe-inline'.
                    var scriptSrc = $"script-src 'self' 'nonce-{nonce}' https://unpkg.com https://cdnjs.cloudflare.com";

                    headers.TryAdd(
                        "Content-Security-Policy",
                        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; form-action 'self'; " +
                        "img-src 'self' data: https:; " +
                        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://unpkg.com https://fonts.googleapis.com; style-src-elem 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://unpkg.com https://fonts.googleapis.com; " +
                        "font-src 'self' data: https://cdn.jsdelivr.net https://unpkg.com https://fonts.gstatic.com; " +
                        $"{scriptSrc}; connect-src 'self'");
                }
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
