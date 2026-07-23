using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Security.Admin;

internal sealed class SupportAccessReadOnlyPageFilter(
    ITenantSupportAccessService supportAccessService,
    ILogger<SupportAccessReadOnlyPageFilter> logger) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        var pagePath = context.ActionDescriptor.ViewEnginePath;

        if (ShouldBlock(pagePath, context.HttpContext.Request.Method)
            && await supportAccessService.IsSupportAccessActiveAsync(context.HttpContext).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Denied {Method} to Razor page {PagePath} during read-only tenant support access",
                context.HttpContext.Request.Method,
                pagePath);
            context.Result = new ForbidResult();
            return;
        }

        await next().ConfigureAwait(false);
    }

    internal static bool ShouldBlock(string pagePath, string method)
    {
        var isAdminPage = pagePath.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase);
        var isSafeMethod = HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method);
        return isAdminPage && !isSafeMethod;
    }
}