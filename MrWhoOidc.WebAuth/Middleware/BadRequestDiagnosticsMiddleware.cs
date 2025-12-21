using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Middleware;

/// <summary>
/// Diagnostics for HTTP 400 responses.
///
/// IMPORTANT: This middleware intentionally avoids logging secrets.
/// - It never logs form values (passwords/tokens).
/// - It only logs sizes and selected metadata.
///
/// Note: Some 400s (e.g., Kestrel rejecting headers/body during parsing) occur before the ASP.NET Core
/// pipeline runs. For those, rely on Kestrel logging categories.
/// </summary>
public sealed class BadRequestDiagnosticsMiddleware(RequestDelegate next, ILogger<BadRequestDiagnosticsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        var path = request.Path.Value ?? string.Empty;
        var method = request.Method;
        var queryLen = request.QueryString.HasValue ? request.QueryString.Value?.Length ?? 0 : 0;
        var contentLen = request.ContentLength;

        var headerCount = request.Headers.Count;
        var cookieHeaderLen = request.Headers.Cookie.ToString().Length;
        var totalHeaderChars = request.Headers.Sum(h => h.Key.Length + h.Value.ToString().Length);

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex)
        {
            // This can occur inside the pipeline (e.g., form parsing). It will not catch Kestrel parse-time rejects.
            logger.LogWarning(ex,
                "HTTP 400 thrown as BadHttpRequestException. Method={Method} Path={Path} TraceId={TraceId} ConnId={ConnId} QueryLen={QueryLen} ContentLen={ContentLen} HeaderCount={HeaderCount} CookieHeaderLen={CookieHeaderLen} TotalHeaderChars={TotalHeaderChars}",
                method,
                path,
                context.TraceIdentifier,
                context.Connection.Id,
                queryLen,
                contentLen ?? -1,
                headerCount,
                cookieHeaderLen,
                totalHeaderChars);
            throw;
        }

        if (context.Response.StatusCode != StatusCodes.Status400BadRequest)
        {
            return;
        }

        var ctxLen = request.Query["ctx"].ToString().Length;
        var returnUrlLen = request.Query["ReturnUrl"].ToString().Length;

        // Best-effort: extract form keys without reading values.
        // Only attempt for /login POST, because other endpoints may be protocol-level.
        string? formKeys = null;
        bool hasAntiForgeryKey = false;
        if (HttpMethods.IsPost(method) && path.Equals("/login", StringComparison.OrdinalIgnoreCase) && request.HasFormContentType)
        {
            try
            {
                request.EnableBuffering();
                var form = await request.ReadFormAsync(context.RequestAborted);
                formKeys = string.Join(",", form.Keys.OrderBy(k => k, StringComparer.Ordinal));
                hasAntiForgeryKey = form.Keys.Contains("__RequestVerificationToken", StringComparer.Ordinal);
                request.Body.Position = 0;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Failed to read form keys for 400 diagnostics. Method={Method} Path={Path} TraceId={TraceId}",
                    method,
                    path,
                    context.TraceIdentifier);
            }
        }

        logger.LogWarning(
            "HTTP 400 response. Method={Method} Path={Path} TraceId={TraceId} ConnId={ConnId} QueryLen={QueryLen} ContentLen={ContentLen} HeaderCount={HeaderCount} CookieHeaderLen={CookieHeaderLen} TotalHeaderChars={TotalHeaderChars} CtxLen={CtxLen} ReturnUrlQueryLen={ReturnUrlLen} HasForm={HasForm} HasAntiforgeryKey={HasAntiForgeryKey} FormKeys={FormKeys}",
            method,
            path,
            context.TraceIdentifier,
            context.Connection.Id,
            queryLen,
            contentLen ?? -1,
            headerCount,
            cookieHeaderLen,
            totalHeaderChars,
            ctxLen,
            returnUrlLen,
            request.HasFormContentType,
            hasAntiForgeryKey,
            formKeys ?? "(n/a)");
    }
}
